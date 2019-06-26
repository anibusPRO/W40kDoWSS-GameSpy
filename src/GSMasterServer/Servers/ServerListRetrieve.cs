﻿using GSMasterServer.Data;
using Reality.Net.Extensions;
using Reality.Net.GameSpy.Servers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Dynamic;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace GSMasterServer.Servers
{
    internal class ServerListRetrieve : Server
    {
        private const string Category = "ServerRetrieve";

        Thread _thread;
        Socket _socket;
        readonly ServerListReport _report;
        readonly ManualResetEvent _reset = new ManualResetEvent(false);

        public ServerListRetrieve(IPAddress listen, ushort port, ServerListReport report)
        {
            _report = report;

           /* _report.Servers.TryAdd("test",
                new GameServer()
                {
                    Valid = true,
                    IPAddress = "192.168.1.20",
                    QueryPort = 29900,
                    country = "RU",
                    hostname = "sF|elamaunt",
                    gamename = "whamdowfr",
                    gamever = "1.1.120",
                    mapname = "Battle Marshes (2)",
                    gametype = "ranked",
                    gamevariant = "pr",
                    //numplayers = 100,
                    //maxplayers = 100,
                    gamemode = "dxp2",
                    password = false,
                    hostport = 16567,
                    natneg = true,
                    numplayersname = 1,
                    maxwaiting = 2,
                    numwaiting = 1,
                    numservers = 2,
                    statechanged = 1
                });*/

			IQueryable<GameServer> servers = _report.Servers.Select(x => x.Value).AsQueryable();

            _thread = new Thread(StartServer)
            {
                Name = "Server Retrieving Socket Thread"
            };

            _thread.Start(new AddressInfo()
            {
                Address = listen,
                Port = port
            });
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            try
            {
                if (disposing)
                {
                    if (_socket != null)
                    {
                        _socket.Close();
                        _socket.Dispose();
                        _socket = null;
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        ~ServerListRetrieve()
        {
            Dispose(false);
        }

        private void StartServer(object parameter)
        {
            AddressInfo info = (AddressInfo)parameter;

            Log(Category, "Starting Server List Retrieval");

            try
            {
                _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
                {
                    SendTimeout = 5000,
                    ReceiveTimeout = 5000,
                    SendBufferSize = 65535,
                    ReceiveBufferSize = 65535
                };
                _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ExclusiveAddressUse, true);
                _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.DontLinger, true);

                _socket.Bind(new IPEndPoint(info.Address, info.Port));
                _socket.Listen(10);
            }
            catch (Exception e)
            {
                LogError(Category, String.Format("Unable to bind Server List Retrieval to {0}:{1}", info.Address, info.Port));
                LogError(Category, e.ToString());
                return;
            }

            while (true)
            {
                _reset.Reset();
                _socket.BeginAccept(AcceptCallback, _socket);
                _reset.WaitOne();
            }
        }

        private void AcceptCallback(IAsyncResult ar)
        {
            _reset.Set();

            Socket listener = (Socket)ar.AsyncState;
            Socket handler = listener.EndAccept(ar);

            SocketState state = new SocketState()
            {
                Socket = handler
            };

            WaitForData(state);
        }

        private void WaitForData(SocketState state)
        {
            Thread.Sleep(10);
            if (state == null || state.Socket == null || !state.Socket.Connected)
                return;

            try
            {
                state.Socket.BeginReceive(state.Buffer, 0, state.Buffer.Length, SocketFlags.None, OnDataReceived, state);
            }
            catch (ObjectDisposedException)
            {
                state.Socket = null;
            }
            catch (SocketException e)
            {
                if (e.SocketErrorCode == SocketError.NotConnected)
                    return;

                LogError(Category, "Error receiving data");
                LogError(Category, String.Format("{0} {1}", e.SocketErrorCode, e));
                return;
            }
        }

        private void OnDataReceived(IAsyncResult async)
        {
            SocketState state = (SocketState)async.AsyncState;

            if (state == null || state.Socket == null || !state.Socket.Connected)
                return;

            try
            {
                // receive data from the socket
                int received = state.Socket.EndReceive(async);
                if (received == 0)
                {
                    // when EndReceive returns 0, it means the socket on the other end has been shut down.
                    return;
                }

                var receivedString = Encoding.ASCII.GetString(state.Buffer, 0, received);

                Log(Category, receivedString);

                ParseRequest(state, receivedString);
            }
            catch (ObjectDisposedException)
            {
                if (state != null)
                    state.Dispose();
                state = null;
                return;
            }
            catch (SocketException e)
            {
                switch (e.SocketErrorCode)
                {
                    case SocketError.ConnectionReset:
                        if (state != null)
                            state.Dispose();
                        state = null;
                        return;
                    case SocketError.Disconnecting:
                        if (state != null)
                            state.Dispose();
                        state = null;
                        return;
                    default:
                        LogError(Category, "Error receiving data");
                        LogError(Category, String.Format("{0} {1}", e.SocketErrorCode, e));
                        if (state != null)
                            state.Dispose();
                        state = null;
                        return;
                }
            }
            catch (Exception e)
            {
                LogError(Category, "Error receiving data");
                LogError(Category, e.ToString());
            }

            // and we wait for more data...
            WaitForData(state);
        }

        private void SendToClient(SocketState state, byte[] data)
        {
            if (state == null)
                return;

            if (state.Socket == null || !state.Socket.Connected)
            {
                state.Dispose();
                state = null;
                return;
            }
            
            try
            {
                state.Socket.BeginSend(data, 0, data.Length, SocketFlags.None, OnSent, state);
            }
            catch (SocketException e)
            {
                LogError(Category, "Error sending data");
                LogError(Category, String.Format("{0} {1}", e.SocketErrorCode, e));
            }
        }

        private void OnSent(IAsyncResult async)
        {
            SocketState state = (SocketState)async.AsyncState;

            if (state == null || state.Socket == null)
                return;

            try
            {
                int sent = state.Socket.EndSend(async);
                Log(Category, String.Format("Sent {0} byte response to: {1}:{2}", sent, ((IPEndPoint)state.Socket.RemoteEndPoint).Address, ((IPEndPoint)state.Socket.RemoteEndPoint).Port));
            }
            catch (SocketException e)
            {
                switch (e.SocketErrorCode)
                {
                    case SocketError.ConnectionReset:
                    case SocketError.Disconnecting:
                        return;
                    default:
                        LogError(Category, "Error sending data");
                        LogError(Category, String.Format("{0} {1}", e.SocketErrorCode, e));
                        return;
                }
            }
            finally
            {
                state.Dispose();
                state = null;
            }
        }

        private bool ParseRequest(SocketState state, string message)
        {
            // d    whamdowfr whamdowfr fkT>_2Cr \hostname\numwaiting\maxwaiting\numservers\numplayersname
            // \u0001\u0012\0\u0001\u0003\u0001\0\0\0whamdowfr\0whamdowfr\0.Ts,PRe`(groupid is null) AND (groupid > 0)\0\\hostname\\gamemode\\hostname\\hostport\\mapname\\password\\gamever\\numplayers\\maxplayers\\score_\\teamplay\\gametype\\gamevariant\\groupid\\numobservers\\maxobservers\\modname\\moddisplayname\\modversion\\devmode\0\0\0\0\u0004

            string[] data = message.Split(new char[] { '\x00' }, StringSplitOptions.RemoveEmptyEntries);

            if (!data[2].Equals("whamdowfr", StringComparison.OrdinalIgnoreCase))
                return false;

            string validate = data[4];
            string filter = null;

            if (validate.Length > 8)
            {
                filter = validate.Substring(8);
                validate = validate.Substring(0,8);
            }

            string[] fields = data[5].Split(new char[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);

            /*string gamename = data[1].ToLowerInvariant();
            string validate = data[2].Substring(0, 8);
            string filter = FixFilter(data[2].Substring(8));
            string[] fields = data[3].Split(new char[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);*/

            //Log(Category, String.Format("Received client request: {0}:{1}", ((IPEndPoint)state.Socket.RemoteEndPoint).Address, ((IPEndPoint)state.Socket.RemoteEndPoint).Port));

            IQueryable<GameServer> servers = _report.Servers.Values.Where(x => x.Valid).AsQueryable();
            /*if (!String.IsNullOrWhiteSpace(filter))
            {
                try
                {
                    //Console.WriteLine(filter);
                    servers = servers.Where(filter);
                    //Console.WriteLine(servers.Count());
                }
                catch (Exception e)
                {
                    LogError(Category, "Error parsing filter");
                    LogError(Category, filter);
                    LogError(Category, e.ToString());
                }
            }*/

            // http://aluigi.altervista.org/papers/gslist.cfg
            /*byte[] key;
            if (gamename == "battlefield2")
                key = DataFunctions.StringToBytes("hW6m9a");
            else if (gamename == "arma2oapc")
                key = DataFunctions.StringToBytes("sGKWik");
            else
                key = DataFunctions.StringToBytes("Xn221z");*/

            byte[] unencryptedServerList = PackServerList(state, servers, fields);
            byte[] encryptedServerList = GSEncoding.Encode(DataFunctions.StringToBytes("pXL838"), DataFunctions.StringToBytes(validate), unencryptedServerList, unencryptedServerList.LongLength);
            SendToClient(state, encryptedServerList);
            return true;
        }

        private static byte[] PackServerList(SocketState state, IEnumerable<GameServer> servers, string[] fields)
        {
            IPEndPoint remoteEndPoint = ((IPEndPoint)state.Socket.RemoteEndPoint);

            byte[] ipBytes = remoteEndPoint.Address.GetAddressBytes();

            byte[] value2 = BitConverter.GetBytes((ushort)remoteEndPoint.Port);
            //byte[] value2 = BitConverter.GetBytes((ushort)6500);
            byte fieldsCount = (byte)fields.Length;

            List<byte> data = new List<byte>();
            data.AddRange(ipBytes);
            data.AddRange(BitConverter.IsLittleEndian ? value2.Reverse() : value2);
            data.Add(fieldsCount);
            data.Add(0);

            foreach (var field in fields)
            {
                data.AddRange(DataFunctions.StringToBytes(field));
                data.AddRange(new byte[] { 0, 0 });
            }

            foreach (var server in servers)
            {
                // commented this stuff out since it caused some issues on testing, might come back to it later and see what's happening...
                // NAT traversal stuff...
                // 126 (\x7E)	= public ip / public port / private ip / private port / icmp ip
                // 115 (\x73)	= public ip / public port / private ip / private port
                // 85 (\x55)	= public ip / public port
                // 81 (\x51)	= public ip / public port
                /*Console.WriteLine(server.IPAddress);
				Console.WriteLine(server.QueryPort);
				Console.WriteLine(server.localip0);
				Console.WriteLine(server.localip1);
				Console.WriteLine(server.localport);
				Console.WriteLine(server.natneg);
				if (!String.IsNullOrWhiteSpace(server.localip0) && !String.IsNullOrWhiteSpace(server.localip1) && server.localport > 0) {
					data.Add(126);
					data.AddRange(IPAddress.Parse(server.IPAddress).GetAddressBytes());
					data.AddRange(BitConverter.IsLittleEndian ? BitConverter.GetBytes((ushort)server.QueryPort).Reverse() : BitConverter.GetBytes((ushort)server.QueryPort));
					data.AddRange(IPAddress.Parse(server.localip0).GetAddressBytes());
					data.AddRange(BitConverter.IsLittleEndian ? BitConverter.GetBytes((ushort)server.localport).Reverse() : BitConverter.GetBytes((ushort)server.localport));
					data.AddRange(IPAddress.Parse(server.localip1).GetAddressBytes());
				} else if (!String.IsNullOrWhiteSpace(server.localip0) && server.localport > 0) {
					data.Add(115);
					data.AddRange(IPAddress.Parse(server.IPAddress).GetAddressBytes());
					data.AddRange(BitConverter.IsLittleEndian ? BitConverter.GetBytes((ushort)server.QueryPort).Reverse() : BitConverter.GetBytes((ushort)server.QueryPort));
					data.AddRange(IPAddress.Parse(server.localip0).GetAddressBytes());
					data.AddRange(BitConverter.IsLittleEndian ? BitConverter.GetBytes((ushort)server.localport).Reverse() : BitConverter.GetBytes((ushort)server.localport));
				} else {*/
                data.Add(81); // it could be 85 as well, unsure of the difference, but 81 seems more common...
                data.AddRange(IPAddress.Parse(server.IPAddress).GetAddressBytes());
                data.AddRange(BitConverter.IsLittleEndian ? BitConverter.GetBytes((ushort)server.QueryPort).Reverse() : BitConverter.GetBytes((ushort)server.QueryPort));
                //}

                data.Add(255);

                for (int i = 0; i < fields.Length; i++)
                {
                    data.AddRange(DataFunctions.StringToBytes(GetField(server, fields[i])));

                    if (i < fields.Length - 1)
                        data.AddRange(new byte[] { 0, 255 });
                }

                data.Add(0);
            }

            data.AddRange(new byte[] { 0, 255, 255, 255, 255 });

            return data.ToArray();
        }

        private static string GetField(GameServer server, string fieldName)
        {
            try
            {
                object value = server.GetType().GetProperty(fieldName).GetValue(server, null);
                if (value == null)
                    return String.Empty;
                else if (value is Boolean)
                    return (bool)value ? "1" : "0";
                else
                    return value.ToString();
            }
            catch (Exception ex)
            {
                return "0";
            }
        }

        private string FixFilter(string filter)
        {
            // escape [
            filter = filter.Replace("[", "[[]");

            // fix an issue in the BF2 main menu where filter expressions aren't joined properly
            // i.e. "numplayers > 0gametype like '%gpm_cq%'"
            // becomes "numplayers > 0 && gametype like '%gpm_cq%'"
            try
            {
                filter = FixFilterOperators(filter);
            }
            catch (Exception e)
            {
                LogError(Category, e.ToString());
            }

            // fix quotes inside quotes
            // i.e. hostname like 'flyin' high'
            // becomes hostname like 'flyin_ high'
            try
            {
                filter = FixFilterQuotes(filter);
            }
            catch (Exception e)
            {
                LogError(Category, e.ToString());
            }

            // fix consecutive whitespace
            filter = Regex.Replace(filter, @"\s+", " ").Trim();

            return filter;
        }

        private static string FixFilterOperators(string filter)
        {
            PropertyInfo[] properties = typeof(GameServer).GetProperties();
            List<string> filterableProperties = new List<string>();

            // get all the properties that aren't "[NonFilter]"
            foreach (var property in properties)
            {
                if (property.GetCustomAttributes(false).Any(x => x.GetType().Name == "NonFilterAttribute"))
                    continue;

                filterableProperties.Add(property.Name);
            }

            // go through each property, see if they exist in the filter,
            // and check to see if what's before the property is a logical operator
            // if it is not, then we slap a && before it
            foreach (var property in filterableProperties)
            {
                IEnumerable<int> indexes = filter.IndexesOf(property);
                foreach (var index in indexes)
                {
                    if (index > 0)
                    {
                        int length = 0;
                        bool hasLogical = IsLogical(filter, index, out length, true) || IsOperator(filter, index, out length, true) || IsGroup(filter, index, out length, true);
                        if (!hasLogical)
                        {
                            filter = filter.Insert(index, " && ");
                        }
                    }
                }
            }
            return filter;
        }

        private static string FixFilterQuotes(string filter)
        {
            StringBuilder newFilter = new StringBuilder(filter);

            for (int i = 0; i < filter.Length; i++)
            {
                int length = 0;
                bool isOperator = IsOperator(filter, i, out length);

                if (isOperator)
                {
                    i += length;
                    bool isInsideString = false;
                    for (; i < filter.Length; i++)
                    {
                        if (filter[i] == '\'' || filter[i] == '"')
                        {
                            if (isInsideString)
                            {
                                // check what's after the quote to see if we terminate the string
                                if (i >= filter.Length - 1)
                                {
                                    // end of string
                                    isInsideString = false;
                                    break;
                                }
                                for (int j = i + 1; j < filter.Length; j++)
                                {
                                    // continue along whitespace
                                    if (filter[j] == ' ')
                                    {
                                        continue;
                                    }
                                    else
                                    {
                                        // if it's a logical operator, then we terminate
                                        bool op = IsLogical(filter, j, out length);
                                        if (op)
                                        {
                                            isInsideString = false;
                                            j += length;
                                            i = j;
                                        }
                                        break;
                                    }
                                }
                                if (isInsideString)
                                {
                                    // and if we're still inside the string, replace the quote with a wildcard character
                                    newFilter[i] = '_';
                                }
                                continue;
                            }
                            else
                            {
                                isInsideString = true;
                            }
                        }
                    }
                }
            }

            return newFilter.ToString();
        }

        private static bool IsOperator(string filter, int i, out int length, bool previous = false)
        {
            bool isOperator = false;
            length = 0;

            if (i < filter.Length - 1)
            {
                string op = filter.Substring(i - (i >= 2 ? (previous ? 2 : 0) : 0), 1);
                if (op == "=" || op == "<" || op == ">")
                {
                    isOperator = true;
                    length = 1;
                }
            }

            if (!isOperator)
            {
                if (i < filter.Length - 2)
                {
                    string op = filter.Substring(i - (i >= 3 ? (previous ? 3 : 0) : 0), 2);
                    if (op == "==" || op == "!=" || op == "<>" || op == "<=" || op == ">=")
                    {
                        isOperator = true;
                        length = 2;
                    }
                }
            }

            if (!isOperator)
            {
                if (i < filter.Length - 4)
                {
                    string op = filter.Substring(i - (i >= 5 ? (previous ? 5 : 0) : 0), 4);
                    if (op.Equals("like", StringComparison.InvariantCultureIgnoreCase))
                    {
                        isOperator = true;
                        length = 4;
                    }
                }
            }

            if (!isOperator)
            {
                if (i < filter.Length - 8)
                {
                    string op = filter.Substring(i - (i >= 9 ? (previous ? 9 : 0) : 0), 8);
                    if (op.Equals("not like", StringComparison.InvariantCultureIgnoreCase))
                    {
                        isOperator = true;
                        length = 8;
                    }
                }
            }

            return isOperator;
        }

        private static bool IsLogical(string filter, int i, out int length, bool previous = false)
        {
            bool isLogical = false;
            length = 0;

            if (i < filter.Length - 2)
            {
                string op = filter.Substring(i - (i >= 3 ? (previous ? 3 : 0) : 0), 2);
                if (op == "&&" || op == "||" || op.Equals("or", StringComparison.InvariantCultureIgnoreCase))
                {
                    isLogical = true;
                    length = 2;
                }
            }

            if (!isLogical)
            {
                if (i < filter.Length - 3)
                {
                    string op = filter.Substring(i - (i >= 4 ? (previous ? 4 : 0) : 0), 3);
                    if (op.Equals("and", StringComparison.InvariantCultureIgnoreCase))
                    {
                        isLogical = true;
                        length = 3;
                    }
                }
            }

            return isLogical;
        }

        private static bool IsGroup(string filter, int i, out int length, bool previous = false)
        {
            bool isGroup = false;
            length = 0;

            if (i < filter.Length - 1)
            {
                string op = filter.Substring(i - (i >= 2 ? (previous ? 2 : 0) : 0), 1);
                if (op == "(" || op == ")")
                {
                    isGroup = true;
                    length = 1;
                }
                if (!isGroup && previous)
                {
                    op = filter.Substring(i - (i >= 1 ? (previous ? 1 : 0) : 0), 1);
                    if (op == "(" || op == ")")
                    {
                        isGroup = true;
                        length = 1;
                    }
                }
            }

            return isGroup;
        }

        private class SocketState : IDisposable
        {
            public Socket Socket = null;
            public byte[] Buffer = new byte[8192];

            public void Dispose()
            {
                Dispose(true);
                GC.SuppressFinalize(this);
            }

            protected virtual void Dispose(bool disposing)
            {
                try
                {
                    if (disposing)
                    {
                        if (Socket != null)
                        {
                            try
                            {
                                Socket.Shutdown(SocketShutdown.Both);
                            }
                            catch (Exception)
                            {
                            }
                            Socket.Close();
                            Socket.Dispose();
                            Socket = null;
                        }
                    }

                    GC.Collect();
                }
                catch (Exception)
                {
                }
            }

            ~SocketState()
            {
                Dispose(false);
            }
        }
    }
}
