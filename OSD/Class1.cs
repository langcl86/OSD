using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using System.Linq;
using System.Xml.Linq;
using System.Xml;

namespace OSD
{
    public class Drivers
    {
        /// <summary>
        /// </summary>
        /// 

        private bool err;

        public bool NetworkEnabled;
        public string DeployDevice;
        public string TargetDevice;
        public string DeviceModel;
        public string DriverCatalog;
        public string DriverPackage;
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
            NetworkEnabled = ping.Send("dell.com").Status == IPStatus.Success;               
        }

        public string getDrivers()
        {
            if (null == DriverPackage) getCatalog();

            string target = TargetDevice;
            string package = DriverPackage;

            try
            {
                while (!err)
                { 
                    if (string.IsNullOrEmpty(package)) throw new ArgumentNullException("DriverPackage Is Not Set");
                
                    string address = package;
                    string filename = Path.GetFileName(package);
                    string cabFile = Path.Combine(target, filename);
                    string ext = Path.GetExtension(package).ToUpper();

                    if (getFile(package, cabFile))
                    {
                        string dest = Path.Combine(TargetDevice, filename.Replace(ext, ""));
                        Directory.CreateDirectory(dest);

                        switch (ext)
                        {
                            case ".CAB":
                                string str = String.Format("{0} -F:* {1}", cabFile, dest);
                                if (newProcess("expand.exe", str).ExitCode != 0) throw new Exception("Failed to extract Driver Catalog");
                                break;

                            case ".EXE":
                                break;

                            default: throw new ArgumentException("Unrecognized file type");
                        }


                        DriverPath = dest;
                    }

                    break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return null;
            }

            return DriverPath;
        }

        public bool getFile(string address, string filename)
        {
            try
            {
                if (string.IsNullOrEmpty(address) ||
                    string.IsNullOrEmpty(filename)) throw new ArgumentNullException();

                if (!NetworkEnabled) throw new IOException();

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

        public void getCatalog()
        {
            string target = TargetDevice;
            err = false;

            try
            {
                string address = @"https://downloads.dell.com/catalog/DriverPackCatalog.cab";
                string filename = Path.GetFileName(address);
                string cabFile = Path.Combine(target, filename);

                if (getFile(address, cabFile))
                {
                    string xml = Path.Combine(target, "catalog.xml");
                    string str = String.Format("{0} -F:* {1}", cabFile, xml);

                    if (newProcess("expand.exe", str).ExitCode != 0) throw new Exception("Failed to extract Driver Catalog");
                    DriverCatalog = xml;
                    getPackage();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                err = true;
            }
        }

        public void getPackage()
        {
            try
            {
                if (string.IsNullOrEmpty(DeviceModel)) throw new ArgumentNullException("DeviceModel Is Not Set");
                string TargetModel = DeviceModel;

                while (!err)
                {
                    FileInfo catalog = new FileInfo(DriverCatalog);
                    if (!catalog.Exists) throw new FileNotFoundException("Catalog not found");

                    XmlDocument xDoc = new XmlDocument();
                    xDoc.Load(catalog.FullName);
                    var nsmgr = new XmlNamespaceManager(xDoc.NameTable);
                    nsmgr.AddNamespace("ns", "openmanage/cm/dm");

                    string pathModel = "//ns:DriverPackage/ns:SupportedSystems/ns:Brand/ns:Model[@name='" + TargetModel + "']";
                    string pathOS = "/ns:SupportedOperatingSystems/ns:OperatingSystem[@osCode='Windows10' and @osArch='x64']";
                    string xPath = String.Format("{0}/../../..{1}/../..", pathModel, pathOS);
                    XmlNode TargetNode = xDoc.SelectNodes(xPath, nsmgr)[0];

                    if (null == TargetNode) throw new Exception("Package Not Found");

                    string Package = String.Join("/", "http://dl.dell.com", GetAttributeValue(TargetNode, "path"));
                    string Version = String.Join(",", GetAttributeValue(TargetNode, "vendorVersion"),
                                                      GetAttributeValue(TargetNode, "dellVersion"));
                    string Date = DateTime.Parse(GetAttributeValue(TargetNode, "dateTime")).ToString("MMMM dd, yyyy");

                    string results = String.Format(
                        "\r\nFound Driver Package - \r\n Release: {0} \r\n Version: {1}\r\n Release Date: {2}\r\n",
                        GetAttributeValue(TargetNode, "releaseID"), Version, Date
                        );

                    Console.WriteLine(results);
                    DriverPackage = Package;
                    getDrivers();
                    return;
                }

                if (err) throw new Exception("Failed to find driver package in catalog");
            }

            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                err = true;
                return;
            }
        }

        private string GetAttributeValue (XmlNode node, string Name)
        {
            string value = String.Empty;
            
            try
            {
                value = node.Attributes[Name].Value;

            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed to get '" + Name + "' attribute value for XML Node.\r\n" + ex.Message);
                err = true;
            }

            return value;
        }
    }
}

