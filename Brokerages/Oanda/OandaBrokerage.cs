﻿/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 * 
 * Licensed under the Apache License, Version 2.0 (the "License"); 
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 * 
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using QuantConnect.Brokerages.Oanda.DataType;
using QuantConnect.Brokerages.Oanda.DataType.Communications;
using QuantConnect.Brokerages.Oanda.Framework;
using QuantConnect.Brokerages.Oanda.Session;
using QuantConnect.Logging;
using QuantConnect.Orders;
using QuantConnect.Securities;
using Order = QuantConnect.Orders.Order;
using QuantConnect.Securities.Forex;

namespace QuantConnect.Brokerages.Oanda
{
    /// <summary>
    /// Oanda Brokerage model.
    /// </summary>
    public class OandaBrokerage : Brokerage
    {
        private readonly IOrderProvider _orderProvider;
        private readonly IHoldingsProvider _holdingsProvider;
        private readonly Environment _environment;
        private readonly string _accessToken;
        private readonly int _accountId;

        private readonly OandaSymbolMapper _symbolMapper = new OandaSymbolMapper();

        private bool _isConnected = false;

        /// <summary>
        /// Initializes a new instance of the <see cref="OandaBrokerage"/> class.
        /// </summary>
        /// <param name="orderProvider">The order provider.</param>
        /// <param name="holdingsProvider">The holdings provider.</param>
        /// <param name="environment">The Oanda environment (Trade or Practice)</param>
        /// <param name="accessToken">The Oanda access token (can be the user's personal access token or the access token obtained with OAuth by QC on behalf of the user)</param>
        /// <param name="accountId">The account identifier.</param>
        public OandaBrokerage(IOrderProvider orderProvider, IHoldingsProvider holdingsProvider, Environment environment, string accessToken, int accountId)
            : base("Oanda Brokerage")
        {
            _orderProvider = orderProvider;
            _holdingsProvider = holdingsProvider;

            if (environment != Environment.Trade && environment != Environment.Practice)
                throw new NotSupportedException("Oanda Environment not supported: " + environment);

            _environment = environment;
            _accessToken = accessToken;
            _accountId = accountId;
        }

        /// <summary>
        /// Returns true if we're currently connected to the broker
        /// </summary>
        public override bool IsConnected
        {
            get { return _isConnected; }
        }

        /// <summary>
        /// Gets all open orders on the account. 
        /// NOTE: The order objects returned do not have QC order IDs.
        /// </summary>
        /// <returns>The open orders returned from Oanda</returns>
        public override List<Order> GetOpenOrders()
        {
            var oandaOrders = GetOrderList();

            var orderList = oandaOrders.Select(ConvertOrder).ToList();
            return orderList;
        }

        /// <summary>
        /// Gets all holdings for the account
        /// </summary>
        /// <returns>The current holdings from the account</returns>
        public override List<Holding> GetAccountHoldings()
        {
            var holdings = GetPositions(_accountId).Select(ConvertHolding).ToList();
            return holdings;
        }

        /// <summary>
        /// Converts the Oanda position into a QuantConnect holding.
        /// </summary>
        /// <param name="position">The position.</param>
        /// <returns></returns>
        protected Holding ConvertHolding(Position position)
        {
            var securityType = _symbolMapper.GetBrokerageSecurityType(position.instrument);

            return new Holding
            {
                Symbol = _symbolMapper.GetLeanSymbol(position.instrument, securityType, Market.Oanda),
                Type = securityType,
                AveragePrice = (decimal)position.avgPrice,
                ConversionRate = 1.0m,
                CurrencySymbol = "$",
                Quantity = position.side == "sell" ? -position.units : position.units
            };
        }

        /// <summary>
        /// Gets the current cash balance for each currency held in the brokerage account
        /// </summary>
        /// <returns>The current cash balance for each currency available for trading</returns>
        public override List<Cash> GetCashBalance()
        {
            var getAccountRequestString = EndpointResolver.ResolveEndpoint(_environment, Server.Account) + "accounts/" + _accountId;
            var accountResponse = MakeRequest<Account>(getAccountRequestString);

            return new List<Cash>
            {
                new Cash(accountResponse.accountCurrency, accountResponse.balance.ToDecimal(),
                    GetUsdConversion(accountResponse.accountCurrency))
            };
        }
        
        
        /// <summary>
        /// Gets the current conversion rate into USD
        /// </summary>
        /// <remarks>Synchronous, blocking</remarks>
        private decimal GetUsdConversion(string currency)
        {
            if (currency == "USD")
            {
                return 1m;
            }

            // determine the correct symbol to choose
            var invertedSymbol = "USD_" + currency;
            var normalSymbol = currency + "_USD";
            var currencyPair = Forex.CurrencyPairs.FirstOrDefault(x => x == invertedSymbol || x == normalSymbol);
            var inverted = invertedSymbol == currencyPair;

            var getCurrencyRequestString = EndpointResolver.ResolveEndpoint(_environment, Server.Rates) + "prices?instruments=" +  (inverted ? invertedSymbol : normalSymbol);
            var accountResponse = MakeRequest<PricesResponse>(getCurrencyRequestString);
            var rate = new decimal(accountResponse.prices.First().ask);
            if (inverted)
            {
                return 1 / rate;
            }
            return rate;
        }

        /// <summary>
        /// Gets the list of available tradable instruments/products from Oanda
        /// </summary>
        /// <returns></returns>
        public List<Instrument> GetInstrumentsAsync(List<string> instrumentNames = null)
        {
            var requestString = EndpointResolver.ResolveEndpoint(_environment, Server.Rates) + "instruments?accountId=" + _accountId;
            if (instrumentNames != null)
            {
                var instrumentsParam = string.Join(",", instrumentNames);
                requestString += "&instruments=" + Uri.EscapeDataString(instrumentsParam);
            }
            var instrumentResponse = MakeRequest<InstrumentsResponse>(requestString);
            var instruments = new List<Instrument>();
            instruments.AddRange(instrumentResponse.instruments);
            return instruments;
        }

        /// <summary>
        /// Places a new order and assigns a new broker ID to the order
        /// </summary>
        /// <param name="order">The order to be placed</param>
        /// <returns>True if the request for a new order has been placed, false otherwise</returns>
        public override bool PlaceOrder(Order order)
        {
            var requestParams = new Dictionary<string, string>
            {
                { "instrument", _symbolMapper.GetBrokerageSymbol(order.Symbol) },
                { "units", Convert.ToInt32(order.AbsoluteQuantity).ToString() }
            };

            PopulateOrderRequestParameters(order, requestParams);

            Log.Trace(order.ToString());


            var priorOrderPositions = GetTradeList(requestParams);

            var postOrderResponse = PostOrderAsync(requestParams);

            if (postOrderResponse != null)
            {
                if (postOrderResponse.tradeOpened != null)
                {
                    order.BrokerId.Add(postOrderResponse.tradeOpened.id);
                }
                
                if (postOrderResponse.tradeReduced != null)
                {
                    order.BrokerId.Add(postOrderResponse.tradeReduced.id);
                }

                if (postOrderResponse.orderOpened != null)
                {
                    order.BrokerId.Add(postOrderResponse.orderOpened.id);
                }

                const int orderFee = 0;
                OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, orderFee) { Status = OrderStatus.Submitted });
            } 
            else
            {
                return false;
            }

            // we need to determine if there was an existing order and wheter we closed it with market orders.

            if (order.Type == OrderType.Market && order.Direction == OrderDirection.Buy)
            {
                //assume that we are opening a new buy market order
                if (postOrderResponse.tradeOpened != null && postOrderResponse.tradeOpened.id > 0)
                {
                    var tradeOpenedId = postOrderResponse.tradeOpened.id;
                    requestParams = new Dictionary<string, string>();
                    var tradeListResponse = GetTradeList(requestParams);
                    if (tradeListResponse.trades.Any(trade => trade.id == tradeOpenedId))
                    {
                        order.BrokerId.Add(tradeOpenedId);
                        const int orderFee = 0;
                        OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, orderFee) { Status = OrderStatus.Filled });
                    }
                }

                if (postOrderResponse.tradesClosed != null)
                {
                    var tradePositionClosedIds = postOrderResponse.tradesClosed.Select(tradesClosed => tradesClosed.id).ToList();
                    var priorOrderPositionIds = priorOrderPositions.trades.Select(previousTrade => previousTrade.id).ToList();
                    var verifyClosedOrder = tradePositionClosedIds.Intersect(priorOrderPositionIds).Count() == tradePositionClosedIds.Count();
                    if (verifyClosedOrder)
                    {
                        const int orderFee = 0;
                        OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, orderFee) { Status = OrderStatus.Filled });
                    }
                }
            }

            if (order.Type == OrderType.Market && order.Direction == OrderDirection.Sell)
            {                
                //assume that we are opening a new buy market order
                if (postOrderResponse.tradeOpened != null && postOrderResponse.tradeOpened.id > 0)
                {
                    var tradeOpenedId = postOrderResponse.tradeOpened.id;
                    requestParams = new Dictionary<string, string>();
                    var tradeListResponse = GetTradeList(requestParams);
                    if (tradeListResponse.trades.Any(trade => trade.id == tradeOpenedId))
                    {
                        order.BrokerId.Add(tradeOpenedId);
                        const int orderFee = 0;
                        OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, orderFee) { Status = OrderStatus.Filled });
                    }
                }

                if (postOrderResponse.tradesClosed != null)
                {
                    var tradePositionClosedIds = postOrderResponse.tradesClosed.Select(tradesClosed => tradesClosed.id).ToList();
                    var priorOrderPositionIds = priorOrderPositions.trades.Select(previousTrade => previousTrade.id).ToList();
                    var verifyClosedOrder = tradePositionClosedIds.Intersect(priorOrderPositionIds).Count() == tradePositionClosedIds.Count();
                    if (verifyClosedOrder)
                    {
                        const int orderFee = 0;
                        OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, orderFee) { Status = OrderStatus.Filled });
                    }
                }
            }
            return true;
        }


        /// <summary>
        /// Updates the order with the same id
        /// </summary>
        /// <param name="order">The new order information</param>
        /// <returns>True if the request was made for the order to be updated, false otherwise</returns>
        public override bool UpdateOrder(Order order)
        {
            Log.Trace("OandaBrokerage.UpdateOrder(): " + order);
            
            if (!order.BrokerId.Any())
            {
                // we need the brokerage order id in order to perform an update
                Log.Trace("OandaBrokerage.UpdateOrder(): Unable to update order without BrokerId.");
                return false;
            }
            
            var requestParams = new Dictionary<string, string>
            {
                { "instrument", _symbolMapper.GetBrokerageSymbol(order.Symbol) },
                { "units", Convert.ToInt32(order.AbsoluteQuantity).ToString() },
            };

            // we need the brokerage order id in order to perform an update
            PopulateOrderRequestParameters(order, requestParams);

            UpdateOrder(order.BrokerId.First(), requestParams);

            return true;
        }

        private static void PopulateOrderRequestParameters(Order order, Dictionary<string, string> requestParams)
        {
            if (order.Direction != OrderDirection.Buy && order.Direction != OrderDirection.Sell)
            {
                throw new Exception("Invalid Order Direction");
            }

            requestParams.Add("side", order.Direction == OrderDirection.Buy ? "buy" : "sell");

            if (order.Type == OrderType.Market)
            {
                requestParams.Add("type", "market");
            }

            if (order.Type == OrderType.Limit)
            {
                requestParams.Add("type", "limit");
                requestParams.Add("price", ((LimitOrder)order).LimitPrice.ToString(CultureInfo.InvariantCulture));
                switch (order.Direction)
                {
                    case OrderDirection.Buy:
                        //Limit Order Does not like Lower Bound Values == Limit Price value
                        //Don't set bounds when placing limit orders. 
                        //Orders can be submitted with lower and upper bounds. If the market price on execution falls outside these bounds, it is considered a "Bounds Violation" and the order is cancelled.
                         break;

                    case OrderDirection.Sell:
                        //Limit Order Does not like Lower Bound Values == Limit Price value
                        //Don't set bounds when placing limit orders. 
                        //Orders can be submitted with lower and upper bounds. If the market price on execution falls outside these bounds, it is considered a "Bounds Violation" and the order is cancelled.
                        break;
                }

                //3 months is the max expiry for Oanda, and OrderDuration.GTC is only currently available
                requestParams.Add("expiry", XmlConvert.ToString(DateTime.Now.AddMonths(3), XmlDateTimeSerializationMode.Utc));
            }

            //this type should contain a stop and a limit to that stop.
            if (order.Type == OrderType.StopLimit)
            {
                requestParams.Add("type", "stop");
                requestParams.Add("price", ((StopLimitOrder)order).StopPrice.ToString(CultureInfo.InvariantCulture));
                switch (order.Direction)
                {
                    case OrderDirection.Buy:
                        //Orders can be submitted with lower and upper bounds. If the market price on execution falls outside these bounds, it is considered a "Bounds Violation" and the order is cancelled.
                        break;
                    case OrderDirection.Sell:
                        //Orders can be submitted with lower and upper bounds. If the market price on execution falls outside these bounds, it is considered a "Bounds Violation" and the order is cancelled.
                        break;
                }

                //3 months is the max expiry for Oanda, and OrderDuration.GTC is only currently available
                requestParams.Add("expiry", XmlConvert.ToString(DateTime.Now.AddMonths(3), XmlDateTimeSerializationMode.Utc));
            }

            if (order.Type == OrderType.StopMarket)
            {
                requestParams.Add("type", "marketIfTouched");
                requestParams.Add("price", ((StopMarketOrder)order).StopPrice.ToString(CultureInfo.InvariantCulture));
                switch (order.Direction)
                {
                    case OrderDirection.Buy:
                        requestParams.Add("upperBound", ((StopMarketOrder)order).StopPrice.ToString(CultureInfo.InvariantCulture));
                        break;
                    case OrderDirection.Sell:
                        requestParams.Add("lowerBound", ((StopMarketOrder)order).StopPrice.ToString(CultureInfo.InvariantCulture));
                        break;
                }

                //3 months is the max expiry for Oanda, and OrderDuration.GTC is only currently available
                requestParams.Add("expiry", XmlConvert.ToString(DateTime.Now.AddMonths(3), XmlDateTimeSerializationMode.Utc));
            }
        }


        /// <summary>
        /// Checks for fill events by registering to the event session to receive events.
        /// </summary>
        private void CheckForFills()
        {
            var session = new EventsSession(this, _accountId);
            session.DataReceived += OnEventReceived;
            session.StartSession();
        }

        private void OnEventReceived(Event data)
        {
            #if DEBUG
            Console.Out.Write("---- On Event Received ----");
            Console.Out.Write(data.transaction);
            #endif
            
            if (data.transaction != null)
            {
                if (data.transaction.type == "ORDER_FILLED")
                {
                    var qcOrder = _orderProvider.GetOrderByBrokerageId(data.transaction.orderId);
                    const int orderFee = 0;
                    var fill = new OrderEvent(qcOrder, DateTime.UtcNow, orderFee, "Oanda Fill Event")
                    {
                        Status = OrderStatus.Filled,
                        FillPrice = (decimal) data.transaction.price,
                        FillQuantity = data.transaction.units
                    };

                    // flip the quantity on sell actions
                    if (qcOrder.Direction == OrderDirection.Sell)
                    {
                        fill.FillQuantity *= -1;
                    }
                    OnOrderEvent(fill);
                }
            }
        }

        /// <summary>
        /// Obtain the active open Trade List from Oanda.
        /// </summary>
        /// <param name="requestParams">the parameters to update (name, value pairs)</param>
        /// <returns></returns>
        public TradesResponse GetTradeList(Dictionary<string, string> requestParams = null)
        {
            var requestString = EndpointResolver.ResolveEndpoint(_environment, Server.Account) + "accounts/" + _accountId + "/trades";
            return MakeRequest<TradesResponse>(requestString, "GET", requestParams);
        }
        
        /// <summary>
        /// Modify the specified order, updating it with the parameters provided
        /// </summary>
        /// <param name="orderId">the identifier of the order to update</param>
        /// <param name="requestParams">the parameters to update (name, value pairs)</param>
        public void UpdateOrder(long orderId, Dictionary<string, string> requestParams)
        {
            var orderRequest = EndpointResolver.ResolveEndpoint(_environment, Server.Account) + "accounts/" + _accountId + "/orders/" + orderId;

            var order = MakeRequest<DataType.Order>(orderRequest);
            if (order != null && order.id > 0)
            {
                var requestString = EndpointResolver.ResolveEndpoint(_environment, Server.Account) + "accounts/" + _accountId + "/orders/" + orderId;
                try
                {
                    MakeRequestWithBody<DataType.Order>(requestString, "PATCH", requestParams);
                     
                } 
                catch (Exception)
                {
                }
                
            } 
            else
            {
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "UpdateFailed", "Failed to update Oanda order id: " + orderId + "."));
                OnOrderEvent(new OrderEvent(ConvertOrder(order), DateTime.UtcNow, 0)
                {
                    Status = OrderStatus.Invalid, Message = string.Format("Order currently does not exist with order id: {0}.", orderId)
                });
            }
        }

        /// <summary>
        /// Retrieves the details for a given order ID
        /// </summary>
        /// <param name="orderId">the id of the order to retrieve</param>
        /// <returns>Order object containing the order details</returns>
        public DataType.Order GetOrderDetails(long orderId)
        {
            var requestString = EndpointResolver.ResolveEndpoint(_environment, Server.Account) + "accounts/" + _accountId + "/orders/" + orderId;
            var order = MakeRequest<DataType.Order>(requestString);
            return order;
        }

        /// <summary>
        /// Retrieves the current rate for each of a list of instruments
        /// </summary>
        /// <param name="instruments">the list of instruments to check</param>
        /// <returns>List of Price objects with the current price for each instrument</returns>
        public List<Price> GetRates(List<Instrument> instruments)
        {
            var requestBuilder = new StringBuilder(EndpointResolver.ResolveEndpoint(_environment, Server.Rates) + "prices?instruments=");
            requestBuilder.Append(string.Join(",", instruments.Select(i => i.instrument)));
            var requestString = requestBuilder.ToString().Trim(',');
            requestString = requestString.Replace(",", "%2C");

            var pricesResponse = MakeRequest<PricesResponse>(requestString);
            var prices = new List<Price>();
            prices.AddRange(pricesResponse.prices);

            return prices;
        }

        /// <summary>
        /// Posts an order on the given account with the given parameters
        /// </summary>
        /// <param name="requestParams">the parameters to use in the request</param>
        /// <returns>PostOrderResponse with details of the results (throws if if fails)</returns>
        public PostOrderResponse PostOrderAsync(Dictionary<string, string> requestParams)
        {
            var requestString = EndpointResolver.ResolveEndpoint(_environment, Server.Account) + "accounts/" + _accountId + "/orders";
            return MakeRequestWithBody<PostOrderResponse>(requestString, "POST", requestParams);
        }

        /// <summary>
        /// Retrieves the list of open orders belonging to the account
        /// </summary>
        /// <param name="requestParams">optional additional parameters for the request (name, value pairs)</param>
        /// <returns>List of Order objects (or empty list, if no orders)</returns>
        public List<DataType.Order> GetOrderList(Dictionary<string, string> requestParams = null)
        {
            var requestString = EndpointResolver.ResolveEndpoint(_environment, Server.Account) + "accounts/" + _accountId + "/orders";
            var ordersResponse = MakeRequest<OrdersResponse>(requestString, "GET", requestParams);
            var orders = new List<DataType.Order>();
            orders.AddRange(ordersResponse.orders);
            return orders;
        }

        /// <summary>
        /// Cancels the order with the specified ID
        /// </summary>
        /// <param name="order">The order to cancel</param>
        /// <returns>True if the request was made for the order to be canceled, false otherwise</returns>
        public override bool CancelOrder(Order order)
        {
            Log.Trace("OandaBrokerage.CancelOrder(): " + order);
            
            if (!order.BrokerId.Any())
            {
                Log.Trace("OandaBrokerage.CancelOrder(): Unable to cancel order without BrokerId.");
                return false;
            }

            foreach (var orderId in order.BrokerId)
            {
                CancelOrder(orderId);
                OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, 0, "Oanda Cancel Order Event") { Status = OrderStatus.Canceled });
            }

            return true;
        }

        private void CancelOrder(long orderId)
        {
            var requestString = EndpointResolver.ResolveEndpoint(_environment, Server.Account) + "accounts/" + _accountId + "/orders/" + orderId;
            MakeRequest<Order>(requestString, "DELETE");
        }
        

        /// <summary>
        /// Connects the client to the broker's remote servers
        /// </summary>
        public override void Connect()
        {
            if (IsConnected) return;

            _isConnected = true;
        }

        /// <summary>
        /// Disconnects the client from the broker's remote servers
        /// </summary>
        public override void Disconnect()
        {
            _isConnected = false;
        }

        private static Stream GetResponseStream(WebResponse response)
        {
            var stream = response.GetResponseStream();
            if (response.Headers["Content-Encoding"] == "gzip")
            {	// if we received a gzipped response, handle that
                if (stream != null) stream = new GZipStream(stream, CompressionMode.Decompress);
            }
            return stream;
        }

        /// <summary>
        /// Initializes a streaming events session which will stream events for the given accounts
        /// </summary>
        /// <param name="accountId">the account IDs you want to stream on</param>
        /// <returns>the WebResponse object that can be used to retrieve the events as they stream</returns>
        public async Task<WebResponse> StartEventsSession(List<int> accountId = null)
        {
            var requestString = EndpointResolver.ResolveEndpoint(_environment, Server.StreamingEvents) + "events";

            if (accountId != null && accountId.Count > 0)
            {
                var accountIds = string.Join(",", accountId);
                requestString += "?accountIds=" + WebUtility.UrlEncode(accountIds);
            }

            var request = WebRequest.CreateHttp(requestString);
            request.Method = "GET";
            request.Headers[HttpRequestHeader.Authorization] = "Bearer " + _accessToken;

            try
            {
                var response = await request.GetResponseAsync();
                return response;
            }
            catch (WebException ex)
            {
                var response = (HttpWebResponse)ex.Response;
                var stream = new StreamReader(response.GetResponseStream());
                var result = stream.ReadToEnd();
                throw new Exception(result);
            }
        }

        /// <summary>
        /// Primary (internal) request handler
        /// </summary>
        /// <typeparam name="T">The response type</typeparam>
        /// <param name="requestString">the request to make</param>
        /// <param name="method">method for the request (defaults to GET)</param>
        /// <param name="requestParams">optional parameters (note that if provided, it's assumed the requestString doesn't contain any)</param>
        /// <returns>response via type T</returns>
        public T MakeRequest<T>(string requestString, string method = "GET", Dictionary<string, string> requestParams = null)
        {
            if (requestParams != null && requestParams.Count > 0)
            {
                var parameters = CreateParamString(requestParams);
                requestString = requestString + "?" + parameters;
            }
            var request = WebRequest.CreateHttp(requestString);
            request.Headers[HttpRequestHeader.Authorization] = "Bearer " + _accessToken;
            request.Headers[HttpRequestHeader.AcceptEncoding] = "gzip, deflate";
            request.Method = method;

            try
            {
                using (var response = request.GetResponse())
                {
                    var serializer = new DataContractJsonSerializer(typeof(T));
                    var stream = GetResponseStream(response);
                    return (T)serializer.ReadObject(stream);
                }
            }
            catch (WebException ex)
            {
                var stream = GetResponseStream(ex.Response);
                var reader = new StreamReader(stream);
                var result = reader.ReadToEnd();
                throw new Exception(result);
            }
        }

        /// <summary>
        /// Primary (internal) asynchronous request handler
        /// </summary>
        /// <typeparam name="T">The response type</typeparam>
        /// <param name="requestString">the request to make</param>
        /// <param name="method">method for the request (defaults to GET)</param>
        /// <param name="requestParams">optional parameters (note that if provided, it's assumed the requestString doesn't contain any)</param>
        /// <returns>response via type T</returns>
        public async Task<T> MakeRequestAsync<T>(string requestString, string method = "GET", Dictionary<string, string> requestParams = null)
        {
            if (requestParams != null && requestParams.Count > 0)
            {
                var parameters = CreateParamString(requestParams);
                requestString = requestString + "?" + parameters;
            }
            var request = WebRequest.CreateHttp(requestString);
            request.Headers[HttpRequestHeader.Authorization] = "Bearer " + _accessToken;
            request.Headers[HttpRequestHeader.AcceptEncoding] = "gzip, deflate";
            request.Method = method;

            try
            {
                using (var response = await request.GetResponseAsync())
                {
                    var serializer = new DataContractJsonSerializer(typeof(T));
                    var stream = GetResponseStream(response);
                    return (T)serializer.ReadObject(stream);
                }
            }
            catch (WebException ex)
            {
                var stream = GetResponseStream(ex.Response);
                var reader = new StreamReader(stream);
                var result = reader.ReadToEnd();
                throw new Exception(result);
            }
        }

        /// <summary>
        /// Secondary (internal) request handler. differs from primary in that parameters are placed in the body instead of the request string
        /// </summary>
        /// <typeparam name="T">response type</typeparam>
        /// <param name="method">method to use (usually POST or PATCH)</param>
        /// <param name="requestParams">the parameters to pass in the request body</param>
        /// <param name="requestString">the request to make</param>
        /// <returns>response, via type T</returns>
        private T MakeRequestWithBody<T>(string requestString, string method, Dictionary<string, string> requestParams)
        {
            // Create the body
            var requestBody = CreateParamString(requestParams);
            var request = WebRequest.CreateHttp(requestString);
            request.Headers[HttpRequestHeader.Authorization] = "Bearer " + _accessToken;
            request.Method = method;
            request.ContentType = "application/x-www-form-urlencoded";

            using (var writer = new StreamWriter(request.GetRequestStream()))
            {
                // Write the body
                writer.WriteAsync(requestBody);
            }

            // Handle the response
            try
            {
                using (var response = request.GetResponse())
                {
                    var serializer = new DataContractJsonSerializer(typeof(T));
                    return (T)serializer.ReadObject(response.GetResponseStream());
                }
            }
            catch (WebException ex)
            {
                var response = (HttpWebResponse)ex.Response;
                var stream = new StreamReader(response.GetResponseStream());
                var result = stream.ReadToEnd();
                throw new Exception(result);
            }
        }
        
        /// <summary>
        /// Retrieves the current non-zero positions for a given account
        /// </summary>
        /// <param name="accountId">positions will be retrieved for this account id</param>
        /// <returns>List of Position objects with the details for each position (or empty list iff no positions)</returns>
        public List<Position> GetPositions(int accountId)
        {
            var requestString = EndpointResolver.ResolveEndpoint(_environment, Server.Account) + "accounts/" + accountId + "/positions";
            var positionResponse = MakeRequest<PositionsResponse>(requestString);
            var positions = new List<Position>();
            positions.AddRange(positionResponse.positions);
            return positions;
        }

        /// <summary>
        /// Helper function to create the parameter string out of a dictionary of parameters
        /// </summary>
        /// <param name="requestParams">the parameters to convert</param>
        /// <returns>string containing all the parameters for use in requests</returns>
        private static string CreateParamString(Dictionary<string, string> requestParams)
        {
            return string.Join("&", requestParams.Select(x => WebUtility.UrlEncode(x.Key) + "=" + WebUtility.UrlEncode(x.Value)));
        }

        /// <summary>
        /// Converts the specified Oanda order into a qc order.
        /// The 'task' will have a value if we needed to issue a rest call for the stop price, otherwise it will be null
        /// </summary>
        protected Order ConvertOrder(DataType.Order order)
        {
            Order qcOrder;
            switch (order.type)
            {
                case "limit":
                    qcOrder = new LimitOrder();
                    if (order.side == "buy")
                    {
                        ((LimitOrder)qcOrder).LimitPrice = Convert.ToDecimal(order.lowerBound);
                    }

                    if (order.side == "sell")
                    {
                        ((LimitOrder)qcOrder).LimitPrice = Convert.ToDecimal(order.upperBound);
                    }

                    break;
                case "stop":
                    qcOrder = new StopLimitOrder();
                    if (order.side == "buy")
                    {
                        ((StopLimitOrder)qcOrder).LimitPrice = Convert.ToDecimal(order.lowerBound);
                    }

                    if (order.side == "sell")
                    {
                        ((StopLimitOrder)qcOrder).LimitPrice = Convert.ToDecimal(order.upperBound);
                    }
                    break;
                case "marketIfTouched":
                    //when market reaches the price sell at market.
                    qcOrder = new StopMarketOrder { Price = Convert.ToDecimal(order.price), StopPrice = Convert.ToDecimal(order.price)};
                    break;
                case "market":
                    qcOrder = new MarketOrder();
                    break;

                default:
                    throw new NotSupportedException("The Oanda order type " + order.type + " is not supported.");
            }
            qcOrder.SecurityType = _symbolMapper.GetBrokerageSecurityType(order.instrument);
            qcOrder.Symbol = _symbolMapper.GetLeanSymbol(order.instrument, qcOrder.SecurityType, Market.Oanda);
            qcOrder.Quantity = ConvertQuantity(order);
            qcOrder.Status = OrderStatus.None;
            qcOrder.BrokerId.Add(order.id);
            var orderByBrokerageId = _orderProvider.GetOrderByBrokerageId(order.id);
            if (orderByBrokerageId != null)
            {
                qcOrder.Id = orderByBrokerageId.Id;
            }
            qcOrder.Duration = OrderDuration.Custom;
            qcOrder.DurationValue = XmlConvert.ToDateTime(order.expiry, XmlDateTimeSerializationMode.Utc);
            qcOrder.Time = XmlConvert.ToDateTime(order.time, XmlDateTimeSerializationMode.Utc);
            
            return qcOrder;
        }

        /// <summary>
        /// Converts the Oanda order quantity into a qc quantity
        /// </summary>
        /// <remarks>
        /// Oanda quantities are always positive and use the direction to denote +/-, where as qc
        /// order quantities determine the direction
        /// </remarks>
        protected int ConvertQuantity(DataType.Order order)
        {
            switch (order.side)
            {
                case "buy":
                    return order.units;

                case "sell":
                    return -order.units;

                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

    }
}
