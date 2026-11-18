using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace LostLightTAS
{
    public sealed class TasServer
    {
        private TcpListener _listener;
        private TcpClient _client;
        private StreamWriter _writer;
        private Thread _acceptThread;
        private volatile bool _running;
        private readonly object _lock = new object();

        public readonly ConcurrentQueue<string> Commands = new ConcurrentQueue<string>();

        public void Start(int port)
        {
            _running = true;
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true };
            _acceptThread.Start();
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                try
                {
                    TcpClient c = _listener.AcceptTcpClient();
                    lock (_lock)
                    {
                        if (_client != null) _client.Close();
                        _client = c;
                        _writer = new StreamWriter(c.GetStream(), new UTF8Encoding(false)) { AutoFlush = true };
                    }
                    var t = new Thread(() => ReadLoop(c)) { IsBackground = true };
                    t.Start();
                }
                catch { if (!_running) break; }
            }
        }

        private void ReadLoop(TcpClient c)
        {
            try
            {
                using (var reader = new StreamReader(c.GetStream(), Encoding.UTF8))
                {
                    string line;
                    while (_running && (line = reader.ReadLine()) != null)
                        if (line.Length > 0) Commands.Enqueue(line.Trim());
                }
            }
            catch { }
            finally
            {
                // Client parti : on libère l'écriture pour que HasClient soit exact (sans toucher un client plus récent).
                lock (_lock)
                {
                    if (_client == c) { _client = null; _writer = null; }
                }
                try { c.Close(); } catch { }
            }
        }

        public bool HasClient
        {
            get { lock (_lock) return _writer != null; }
        }

        // Ligne libre (EVENT ..., INFO ...) envoyée à l'appli.
        public void SendLine(string line)
        {
            lock (_lock)
            {
                if (_writer == null) return;
                try { _writer.WriteLine(line); }
                catch { _writer = null; }
            }
        }

        public void SendState(int frame, float px, float py, float pz, float vx, float vy, float vz, string scene)
        {
            lock (_lock)
            {
                if (_writer == null) return;
                try
                {
                    var inv = System.Globalization.CultureInfo.InvariantCulture;
                    _writer.WriteLine(string.Format(inv,
                        "STATE {0} {1:0.####} {2:0.####} {3:0.####} {4:0.####} {5:0.####} {6:0.####} {7}",
                        frame, px, py, pz, vx, vy, vz, scene));
                }
                catch { _writer = null; }
            }
        }

        public void Stop()
        {
            _running = false;
            try { if (_listener != null) _listener.Stop(); } catch { }
            lock (_lock) { try { if (_client != null) _client.Close(); } catch { } }
        }
    }
}