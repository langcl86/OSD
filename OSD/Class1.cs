using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using System.Xml.Linq;
using System.Xml;

namespace OSD
{
    public class Drivers
    {
        /// <summary>
        /// TODO: Parse XML Catalog And Identify Driver Pack Path For DeviceModel
        /// TODO: Identify Driver Pack Type (CAB/EXE) - Extract Driver Pack
        /// </summary>
        /// 

        private bool err;

        public bool NetworkEnabled;
        public string DeployDevice;
        public string TargetDevice;
        public string DeviceModel;
        public string DriverCatalog;
        public string DriverPath;
        public string DriverOS;

        public Drivers()
        {
            Console.WriteLine("**WARNING**\r\nTarget Device Not Set");
            NetCheck();
        }

        public Drivers(string Target)
        {
            TargetDevice = Target;
            NetCheck();
        }

        public void NetCheck()
        {
            Ping ping = new Ping();
            NetworkEnabled = ping.Send("google.com").Status == IPStatus.Success;               
        }

        public string getCatalog()
        {
            string target = TargetDevice;

            try
            {
                string address = @"https://downloads.dell.com/catalog/DriverPackCatalog.cab";
                string cabFile = Path.Combine(target, Path.GetFileName(address));

                if (getFile(address, cabFile))
                {
                    string xml = Path.Combine(target, "catalog.xml");
                    string str = String.Format("{0} -F:* {1}", cabFile, xml);

                    if (newProcess("expand.exe", str).ExitCode != 0) throw new Exception("Failed to extract Driver Catalog");
                    DriverCatalog = xml;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return null;
            }

            return DriverCatalog;
        }

        public bool getFile(string address, string filename)
        {
            try
            {
                if (string.IsNullOrEmpty(address) ||
                    string.IsNullOrEmpty(filename)) throw new ArgumentNullException();

                System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12;

                Uri uri = new Uri(address);
                WebClient client = new WebClient();
                client.DownloadProgressChanged += new DownloadProgressChangedEventHandler(inProgress);
                client.DownloadFileCompleted += new AsyncCompletedEventHandler(Completed);
                client.DownloadFileAsync(uri, filename);
                while (client.IsBusy) Thread.Sleep(1000);

                if (err) throw new Exception("Download FAILED");
                return true;
            }

            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return false;
            }

        }

        private void inProgress(object sender, DownloadProgressChangedEventArgs e)
        {
            Console.WriteLine("{0}    downloaded {1} of {2} MB. {3} % complete...",
            (string)e.UserState,
            e.BytesReceived / Math.Pow(1024, 2),
            e.TotalBytesToReceive / Math.Pow(1024, 2),
            e.ProgressPercentage);
        }

        private void Completed(object sender, AsyncCompletedEventArgs e)
        {
            if (!e.Cancelled) Console.WriteLine("Download Completed");
            else err = true;
        }

        private Process newProcess(string filename, string args)
        {
            ProcessStartInfo si = new ProcessStartInfo();
            si.FileName = filename;
            si.Arguments = args;
            si.UseShellExecute = false;

            Process p = new Process();
            p.StartInfo = si;
            p.Start();
            p.WaitForExit();
            return p;
        }
    }


}

