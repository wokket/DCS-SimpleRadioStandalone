﻿using Ciribob.DCS.SimpleRadio.Standalone.Common;
using Newtonsoft.Json;
using NLog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;

namespace Ciribob.DCS.SimpleRadio.Standalone.Server
{

    // State object for reading client data asynchronously
    public class StateObject
    {
        // Client  socket.
        public Socket workSocket = null;
        // Size of receive buffer.
        public const int BufferSize = 1024;
        // Receive buffer.
        public byte[] buffer = new byte[BufferSize];
        // Received data string.
        public StringBuilder sb = new StringBuilder();

        public string guid = null;
    }

    class ServerSync
    {

        private static Logger _logger = LogManager.GetCurrentClassLogger();
        // Thread signal.
        public static ManualResetEvent _allDone = new ManualResetEvent(false);

        Socket listener;

        ConcurrentDictionary<String, SRClient> _clients = new ConcurrentDictionary<String, SRClient>();

        public ServerSync(ConcurrentDictionary<String, SRClient> connectedClients)
        {
            this._clients = connectedClients;

            //   this.clientIndexList.Capacity = 100000;
        }

        public void StartListening()
        {

            IPAddress ipAddress = new IPAddress(0);
            IPEndPoint localEndPoint = new IPEndPoint(ipAddress, 5002);

            // Create a TCP/IP socket.
            listener = new Socket(AddressFamily.InterNetwork,
               SocketType.Stream, ProtocolType.Tcp);

            // Bind the socket to the local endpoint and listen for incoming connections.
            try
            {
                listener.Bind(localEndPoint);
                listener.Listen(100);

                while (true)
                {
                    // Set the event to nonsignaled state.
                    _allDone.Reset();

                    // Start an asynchronous socket to listen for connections.
                    _logger.Info("Waiting for a connection...");
                    listener.BeginAccept(
                        new AsyncCallback(AcceptCallback),
                        listener);

                    // Wait until a connection is made before continuing.
                    _allDone.WaitOne();
                }

            }
            catch (Exception e)
            {
                _logger.Error(e, "Server Listen error");

            }
        }

        public void AcceptCallback(IAsyncResult ar)
        {
            // Signal the main thread to continue.
            _allDone.Set();

            try
            {
                // Get the socket that handles the client request.
                Socket listener = (Socket)ar.AsyncState;
                Socket handler = listener.EndAccept(ar);
                handler.NoDelay = true;

                // Create the state object.
                StateObject state = new StateObject();
                state.workSocket = handler;
                handler.BeginReceive(state.buffer, 0, StateObject.BufferSize, 0,
                    new AsyncCallback(ReadCallback), state);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error accepting socket");
            }


        }

        public void HandleDisconnect(StateObject state)
        {
            _logger.Info("Disconnecting Client");

            if (state != null && state.guid != null)
            {
                //removed
                SRClient client;
                _clients.TryRemove(state.guid, out client);

                if (client != null)
                {

                    _logger.Info("Removed Client " + state.guid);
                }
            }

            try
            {
                state.workSocket.Close();
            }
            catch (Exception ex)
            {
                _logger.Info(ex, "Exception closing socket after disconnect");
            }

        }

        public void ReadCallback(IAsyncResult ar)
        {

            // Retrieve the state object and the handler socket
            // from the asynchronous state object.
            StateObject state = (StateObject)ar.AsyncState;
            Socket handler = state.workSocket;

            try
            {



                // Read data from the client socket. 
                int bytesRead = handler.EndReceive(ar);

                if (bytesRead > 0)
                {
                    // There  might be more data, so store the data received so far.
                    state.sb.Append(Encoding.ASCII.GetString(
                        state.buffer, 0, bytesRead));

                    String content = state.sb.ToString();
                    if (content.EndsWith("\n"))
                    {
                        //Console.WriteLine("Read {0} bytes from socket. \n Data : {1}",
                        //   content.Length, content);

                        try
                        {
                            NetworkMessage message = JsonConvert.DeserializeObject<NetworkMessage>(content);

                            HandleMessage(state, message);

                        }
                        catch (Exception ex)
                        {
                            _logger.Error(ex, "Server - Client Exception reading");

                        }

                        //clear the state buffer
                        state.sb.Clear();

                        handler.BeginReceive(state.buffer, 0, StateObject.BufferSize, 0,
                   new AsyncCallback(ReadCallback), state);

                    }
                    else
                    {
                        // Not all data received. Get more.
                        handler.BeginReceive(state.buffer, 0, StateObject.BufferSize, 0,
                        new AsyncCallback(ReadCallback), state);
                    }
                }
                else
                {
                    HandleDisconnect(state);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error reading from socket. Disconnecting ");

                HandleDisconnect(state);
            }
        }

        public void HandleMessage(StateObject state, NetworkMessage message)
        {
            try
            {
                var clientIp = (IPEndPoint)state.workSocket.RemoteEndPoint;

                //  logger.Info("Received From " + clientIp.Address + " " + clientIp.Port);
                // logger.Info("Recevied: " + message.MsgType);

                switch (message.MsgType)
                {
                    case NetworkMessage.MessageType.PING:

                        HandleClientPing(message);

                        break;
                    case NetworkMessage.MessageType.SYNC:

                        if (!_clients.ContainsKey(message.ClientGuid))
                        {

                            SRClient srClient = new SRClient() { ClientGuid = message.ClientGuid, ClientSocket = state.workSocket };

                            //add to proper list
                            _clients[message.ClientGuid] = srClient;

                            state.guid = message.ClientGuid;
                        }
                        else
                        {
                            //Do nothiing
                        }

                        HandleRadioSync(clientIp, state.workSocket, message);

                        break;
                    default:
                        _logger.Warn("Recevied unknown message type");
                        break;
                }

            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Exception Handling Message " + ex.Message);
            }
        }

        private void HandleClientPing(NetworkMessage message)
        {
            if (_clients.ContainsKey(message.ClientGuid))
            {
                var client = _clients[message.ClientGuid];

                if (client != null)
                {
                    client.LastUpdate = System.Environment.TickCount;
                    //send update to everyone

                    NetworkMessage replyMessage = new NetworkMessage();
                    replyMessage.MsgType = NetworkMessage.MessageType.PING;
                    replyMessage.ClientGuid = client.ClientGuid;

                    foreach (var clientToSent in this._clients)
                    {
                        Send(clientToSent.Value.ClientSocket, replyMessage);
                    }
                }
            }
        }

        private void HandleRadioSync(IPEndPoint clientIp, Socket clientSocket, NetworkMessage message)
        {
            //store new client



            NetworkMessage replyMessage = new NetworkMessage();
            replyMessage.MsgType = NetworkMessage.MessageType.SYNC;

            replyMessage.Clients = new List<SRClient>();

            foreach (var clientToSent in this._clients)
            {
                replyMessage.Clients.Add(clientToSent.Value);
            }

            Send(clientSocket, replyMessage);
        }

        private static void Send(Socket handler, NetworkMessage message)
        {
            try
            {
                // Convert the string data to byte data using ASCII encoding.
                byte[] byteData = System.Text.Encoding.ASCII.GetBytes(JsonConvert.SerializeObject(message) + "\n");

                // Begin sending the data to the remote device.
                handler.BeginSend(byteData, 0, byteData.Length, 0,
                    new AsyncCallback(SendCallback), handler);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Exception Sending Message " + ex.Message);
            }
        }

        private static void SendCallback(IAsyncResult ar)
        {
            try
            {
                // Retrieve the socket from the state object.
                Socket handler = (Socket)ar.AsyncState;

                // Complete sending the data to the remote device.
                int bytesSent = handler.EndSend(ar);
                //  Console.WriteLine("Sent {0} bytes to client.", bytesSent);

            }
            catch (Exception e)
            {
                _logger.Error(e, "Exception SendCallback ");
            }
        }

        public void RequestStop()
        {
            try
            {

                foreach (var client in _clients)
                {
                    try
                    {
                        client.Value.ClientSocket.Close();
                    }
                    catch (Exception ex) { }
                }
                listener.Close();

                _clients.Clear();

            }
            catch (Exception ex) { }
        }


    }
}