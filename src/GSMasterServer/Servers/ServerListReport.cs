﻿using Alivate;
using GSMasterServer.Data;
using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace GSMasterServer.Servers
{
    internal class ServerListReport : Server
    {
        const string Category = "ServerReport";
        const int BufferSize = 65535;

        public readonly ConcurrentDictionary<string, GameServer> Servers;

        string[] _modWhitelist;
        IPAddress[] _plasmaServers;

        Thread _thread;

        Socket _socket;
        SocketAsyncEventArgs _socketReadEvent;
        byte[] _socketReceivedBuffer;
        
        public ServerListReport(IPAddress listen, ushort port)
        {
            GeoIP.Initialize(Log, Category);

            Servers = new ConcurrentDictionary<string, GameServer>();

            _thread = new Thread(StartServer)
            {
                Name = "Server Reporting Socket Thread"
            };
            _thread.Start(new AddressInfo()
            {
                Address = listen,
                Port = port
            });

           /* new Thread(StartCleanup)
            {
                Name = "Server Reporting Cleanup Thread"
            }.Start();*/

            new Thread(StartDynamicInfoReload)
            {
                Name = "Dynamic Info Reload Thread"
            }.Start();
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

        ~ServerListReport()
        {
            Dispose(false);
        }

        private void StartServer(object parameter)
        {
            AddressInfo info = (AddressInfo)parameter;

            Log(Category, "Starting Server List Reporting");

            try
            {
                _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
                {
                    SendTimeout = 5000,
                    ReceiveTimeout = 5000,
                    SendBufferSize = BufferSize,
                    ReceiveBufferSize = BufferSize
                };

                _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ExclusiveAddressUse, true);
                _socket.Bind(new IPEndPoint(info.Address, info.Port));

                _socketReadEvent = new SocketAsyncEventArgs()
                {
                    RemoteEndPoint = new IPEndPoint(IPAddress.Any, 0)
                };

                _socketReceivedBuffer = new byte[BufferSize];
                _socketReadEvent.SetBuffer(_socketReceivedBuffer, 0, BufferSize);
                _socketReadEvent.Completed += OnDataReceived;
            }
            catch (Exception e)
            {
                LogError(Category, String.Format("Unable to bind Server List Reporting to {0}:{1}", info.Address, info.Port));
                LogError(Category, e.ToString());
                return;
            }

            WaitForData();
        }

        private void StartCleanup(object parameter)
        {
            while (true)
            {
                foreach (var key in Servers.Keys)
                {
                    GameServer value;

                    if (Servers.TryGetValue(key, out value))
                    {
                        if (value.LastPing < DateTime.UtcNow - TimeSpan.FromSeconds(30))
                        {
                            Log(Category, String.Format("Removing old server at: {0}", key));

                            GameServer temp;
                            Servers.TryRemove(key, out temp);
                        }
                    }
                }

                Thread.Sleep(10000);
            }
        }

        private void StartDynamicInfoReload(object obj)
        {
            while (true)
            {
                // the modwhitelist.txt file is for only allowing servers running certain mods to register with the master server
                // by default, this is pr or pr_* (it's really pr!_%, since % is wildcard, _ is placeholder, ! is escape)
                // # is for comments
                // you either want to utilize modwhitelist.txt or hardcode the default if you're using another mod...
                // put each mod name on a new line
                // to allow all mods, just put a single %
                if (File.Exists("modwhitelist.txt"))
                {
                    Log(Category, "Loading mod whitelist");
                    _modWhitelist = File.ReadAllLines("modwhitelist.txt").Where(x => !String.IsNullOrWhiteSpace(x) && !x.Trim().StartsWith("#")).ToArray();
                }
                else
                {
                    _modWhitelist = new string[] { "pr", "pr!_%" };
                }

                // plasma servers (bf2_plasma = 1) makes servers show up in green in the server list in bf2's main menu (or blue in pr's menu)
                // this could be useful to promote servers and make them stand out, sponsored servers, special events, stuff like that
                // put in the ip address of each server on a new line in plasmaservers.txt, and make them stand out
                if (File.Exists("plasmaservers.txt"))
                {
                    Log(Category, "Loading plasma servers");
                    _plasmaServers = File.ReadAllLines("plasmaservers.txt").Select(x =>
                    {
                        IPAddress address;
                        if (IPAddress.TryParse(x, out address))
                            return address;
                        else
                            return null;
                    }).Where(x => x != null).ToArray();
                }
                else
                {
                    _plasmaServers = new IPAddress[0];
                }

                GC.Collect();
                
                Thread.Sleep(5 * 60 * 1000);
            }
        }

        private void WaitForData()
        {
            Thread.Sleep(10);
            GC.Collect();

            try
            {
                _socket.ReceiveFromAsync(_socketReadEvent);
            }
            catch (SocketException e)
            {
                LogError(Category, "Error receiving data");
                LogError(Category, e.ToString());
                return;
            }
        }

        private void OnDataReceived(object sender, SocketAsyncEventArgs e)
        {
            try
            {
                IPEndPoint remote = (IPEndPoint)e.RemoteEndPoint;

                byte[] receivedBytes = new byte[e.BytesTransferred];
                Array.Copy(e.Buffer, e.Offset, receivedBytes, 0, e.BytesTransferred);

                var str = Encoding.ASCII.GetString(receivedBytes);

                // there by a bunch of different message formats...
                Log(Category, str);

                // \u0003\u0015?{?localip0\0192.168.159.1\0localip1\0192.168.58.1\0localip2\0192.168.97.2\0localip3\0192.168.56.1\0localip4\0192.168.1.21\0localport\06112\0natneg\01\0statechanged\02\0gamename\0whammer40kdcam\0\0
                // ?{?localip0 192.168.159.1 localip1 192.168.58.1 localip2 192.168.97.2 localip3 192.168.56.1 localip4 192.168.1.21 localport 6112 natneg 1 statechanged 2 gamename whammer40kdcam

                if (str == "\t\0\0\0\0whamdowfr\0" || str == "\t\0\0\0\0whammer40kdc\0")
                {
                    // the initial message is basically the gamename, 0x09 0x00 0x00 0x00 0x00 whamdowfr
                    // reply back a good response
                    byte[] response = new byte[] { 0xfe, 0xfd, 0x09, 0x00, 0x00, 0x00, 0x00 };

                    var resp = Encoding.UTF8.GetString(response);
                    Log(Category, "Responce  " + resp);
                    _socket.SendTo(response, remote);
                }
                else if (receivedBytes.Length > 5 && receivedBytes[0] == 0x03)
                {
                    // this is where server details come in, it starts with 0x03, it happens every 60 seconds or so

                    byte[] uniqueId = new byte[4];
                    Array.Copy(receivedBytes, 1, uniqueId, 0, 4);

                    if (!ParseServerDetails(remote, receivedBytes.Skip(5).ToArray()))
                    {
                        // this should be some sort of proper encrypted challenge, but for now i'm just going to hard code it because I don't know how the encryption works...
                        //byte[] response = new byte[] { 0xfe, 0xfd, 0x01, uniqueId[0], uniqueId[1], uniqueId[2], uniqueId[3], 0x44, 0x3d, 0x73, 0x7e, 0x6a, 0x59, 0x30, 0x30, 0x37, 0x43, 0x39, 0x35, 0x41, 0x42, 0x42, 0x35, 0x37, 0x34, 0x43, 0x43, 0x00 };

                        // Рабочий вариант server challenge из кода сервака для Цивы 4
                        byte[] response = new byte[] { 0xfe, 0xfd, 0x01, uniqueId[0], uniqueId[1], uniqueId[2], uniqueId[3], 0x41, 0x43, 0x4E, 0x2B, 0x78, 0x38, 0x44, 0x6D, 0x57, 0x49, 0x76, 0x6D, 0x64, 0x5A, 0x41, 0x51, 0x45, 0x37, 0x68, 0x41, 0x00 };
                        
                        _socket.SendTo(response, remote);
                    }
                }
                else if (receivedBytes.Length > 5 && receivedBytes[0] == 0x01)
                {
                    // this is a challenge response, it starts with 0x01

                    byte[] uniqueId = new byte[4];
                    Array.Copy(receivedBytes, 1, uniqueId, 0, 4);

                    // confirm against the hardcoded challenge
                    //byte[] validate = new byte[] { 0x72, 0x62, 0x75, 0x67, 0x4a, 0x34, 0x34, 0x64, 0x34, 0x7a, 0x2b, 0x66, 0x61, 0x78, 0x30, 0x2f, 0x74, 0x74, 0x56, 0x56, 0x46, 0x64, 0x47, 0x62, 0x4d, 0x7a, 0x38, 0x41, 0x00 };
                    

                    byte[] validate = Encoding.UTF8.GetBytes("Iare43/78WkOVaU1Aanv8vrXbSwA\0"); //new byte[] { 0x41, 0x42, 0x4A, 0x36, 0x47, 0x74, 0x4E, 0x42, 0x35, 0x6D, 0x55, 0x59, 0x48, 0x7A, 0x30, 0x2B, 0x78, 0x34, 0x38, 0x46, 0x36, 0x34, 0x76, 0x4A, 0x54, 0x51, 0x45, 0x41, 0x00 };

                    byte[] clientResponse = new byte[validate.Length];
                    Array.Copy(receivedBytes, 5, clientResponse, 0, clientResponse.Length);

                    var resStr = Encoding.UTF8.GetString(clientResponse);

                    // if we validate, reply back a good response
                    if (clientResponse.SequenceEqual(validate))
                    {
                        byte[] response = new byte[] { 0xfe, 0xfd, 0x0a, uniqueId[0], uniqueId[1], uniqueId[2], uniqueId[3] };
                        _socket.SendTo(response, remote);

                        AddValidServer(remote);
                    }
                }
                else if (receivedBytes.Length == 5 && receivedBytes[0] == 0x08)
                {
                    // this is a server ping, it starts with 0x08, it happens every 20 seconds or so

                    byte[] uniqueId = new byte[4];
                    Array.Copy(receivedBytes, 1, uniqueId, 0, 4);

                    RefreshServerPing(remote);
                }
            }
            catch (Exception ex)
            {
                LogError(Category, ex.ToString());
            }

            WaitForData();
        }

        private void RefreshServerPing(IPEndPoint remote)
        {
            string key = String.Format("{0}:{1}", remote.Address, remote.Port);
            if (Servers.ContainsKey(key))
            {
                GameServer value;
                if (Servers.TryGetValue(key, out value))
                {
                    value.LastPing = DateTime.UtcNow;
                    Servers[key] = value;
                }
            }
        }

        private bool ParseServerDetails(IPEndPoint remote, byte[] data)
        {
            string key = String.Format("{0}:{1}", remote.Address, remote.Port);
            string receivedData = Encoding.UTF8.GetString(data);

            //Console.WriteLine(receivedData.Replace("\x00", "\\x00").Replace("\x02", "\\x02"));

            // split by 000 (info/player separator) and 002 (players/teams separator)
            // the players/teams separator is really 00, but because 00 may also be used elsewhere (an empty value for example), we hardcode it to 002
            // the 2 is the size of the teams, for BF2 this is always 2.
            string[] sections = receivedData.Split(new string[] { "\x00\x00\x00", "\x00\x00\x02" }, StringSplitOptions.None);

            //Console.WriteLine(sections.Length);

            if (sections.Length != 3 && !receivedData.EndsWith("\x00\x00"))
                return true; // true means we don't send back a response

            string serverVars = sections[0];
            //string playerVars = sections[1];
            //string teamVars = sections[2];

            string[] serverVarsSplit = serverVars.Split(new string[] { "\x00" }, StringSplitOptions.None);

            GameServer server = new GameServer()
            {
                Valid = false,
                IPAddress = remote.Address.ToString(),
                QueryPort = remote.Port,
                LastRefreshed = DateTime.UtcNow,
                LastPing = DateTime.UtcNow
            };

            // set the country based off ip address
            if (GeoIP.Instance == null || GeoIP.Instance.Reader == null)
            {
                server.country = "??";
            }
            else
            {
                try
                {
                    server.country = GeoIP.Instance.Reader.Omni(server.IPAddress).Country.IsoCode.ToUpperInvariant();
                }
                catch (Exception e)
                {
                    LogError(Category, e.ToString());
                    server.country = "??";
                }
            }

            for (int i = 0; i < serverVarsSplit.Length - 1; i += 2)
            {
                PropertyInfo property = server.GetType().GetProperty(serverVarsSplit[i]);

                if (property == null)
                    continue;

                if (property.Name == "hostname")
                {
                    // strip consecutive whitespace from hostname
                    property.SetValue(server, Regex.Replace(serverVarsSplit[i + 1], @"\s+", " ").Trim(), null);
                }
                else if (property.Name == "bf2_plasma")
                {
                    // set plasma to true if the ip is in plasmaservers.txt
                    if (_plasmaServers.Any(x => x.Equals(remote.Address)))
                        property.SetValue(server, true, null);
                    else
                        property.SetValue(server, false, null);
                }
                else if (property.Name == "bf2_ranked")
                {
                    // we're always a ranked server (helps for mods with a default bf2 main menu, and default filters wanting ranked servers)
                    property.SetValue(server, true, null);
                }
                else if (property.Name == "bf2_pure")
                {
                    // we're always a pure server
                    property.SetValue(server, true, null);
                }
                else if (property.PropertyType == typeof(Boolean))
                {
                    // parse string to bool (values come in as 1 or 0)
                    int value;
                    if (Int32.TryParse(serverVarsSplit[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                    {
                        property.SetValue(server, value != 0, null);
                    }
                }
                else if (property.PropertyType == typeof(Int32))
                {
                    // parse string to int
                    int value;
                    if (Int32.TryParse(serverVarsSplit[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                    {
                        property.SetValue(server, value, null);
                    }
                }
                else if (property.PropertyType == typeof(Double))
                {
                    // parse string to double
                    double value;
                    if (Double.TryParse(serverVarsSplit[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                    {
                        property.SetValue(server, value, null);
                    }
                }
                else if (property.PropertyType == typeof(String))
                {
                    // parse string to string
                    property.SetValue(server, serverVarsSplit[i + 1], null);
                }
            }

            if (String.IsNullOrWhiteSpace(server.gamename) || !server.gamename.Equals("whamdowfr", StringComparison.InvariantCultureIgnoreCase))
            {
                // only allow servers with a gamename of battlefield2
                return true; // true means we don't send back a response
            }
          /*  else if (String.IsNullOrWhiteSpace(server.gamevariant) || !_modWhitelist.ToList().Any(x => SQLMethods.EvaluateIsLike(server.gamevariant, x)))
            {
                // only allow servers with a gamevariant of those listed in modwhitelist.txt, or (pr || pr_*) by default
                return true; // true means we don't send back a response
            }*/

            // you've got to have all these properties in order for your server to be valid
            if (!String.IsNullOrWhiteSpace(server.hostname) &&
                !String.IsNullOrWhiteSpace(server.gamevariant) &&
                !String.IsNullOrWhiteSpace(server.gamever) &&
                !String.IsNullOrWhiteSpace(server.gametype) &&
                !String.IsNullOrWhiteSpace(server.mapname) &&
                server.hostport > 1024 && server.hostport <= UInt16.MaxValue &&
                server.maxplayers > 0)
            {
                server.Valid = true;
            }

            // if the server list doesn't contain this server, we need to return false in order to send a challenge
            // if the server replies back with the good challenge, it'll be added in AddValidServer
            if (!Servers.ContainsKey(key))
                return false;

            Servers.AddOrUpdate(key, server, (k, old) =>
            {
                if (!old.Valid && server.Valid)
                {
                    Log(Category, String.Format("Added new server at: {0}:{1} ({2}) ({3})", server.IPAddress, server.QueryPort, server.country, server.gamevariant));
                }

                return server;
            });

            return true;
        }

        private void AddValidServer(IPEndPoint remote)
        {
            string key = String.Format("{0}:{1}", remote.Address, remote.Port);
            GameServer server = new GameServer()
            {
                Valid = false,
                IPAddress = remote.Address.ToString(),
                QueryPort = remote.Port,
                LastRefreshed = DateTime.UtcNow,
                LastPing = DateTime.UtcNow
            };

            Servers.AddOrUpdate(key, server, (k, old) =>
            {
                return server;
            });
        }
    }
}
