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
        ///      This Class is used to obtain Dell device driver packs for the purposes of OS Deployment
        ///      Ref: https://www.dell.com/support/kbdoc/en-us/000122176/driver-pack-catalog
        /// </summary>
        /// 

        public bool error;
        public bool NetworkEnabled;
        public string TargetOS;
        public string TargetModel;
        public string TargetDevice;
        public string DeployDevice;
        public string DriverCatalog;
        public string DriverPackage;
        public string DriverPath;

        public Drivers()
        {
            Console.WriteLine("\r\n**WARNING**\r\nTarget Device Not Set\r\n");
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

        public void GetDrivers()
        {
            /// <synopsis>
            ///     Obtains driver pack for TargetModel.
            ///     Driver catalog is download and extracted to TargetDevice.
            ///     Driver pack is downloaded to TargetDevice As .CAB
            ///     Driver pack is extracted to DriverPath
            ///     DriverPackage is the location of the driver pack on the Dell server.
            /// </synopsis>
            /// 

            if (null == DriverPackage) getCatalog();

            string target = TargetDevice;
            string package = DriverPackage;

            try
            {
                while (!error)
                { 
                    if (string.IsNullOrEmpty(package)) throw new ArgumentNullException("DriverPackage Is Not Set");
                
                    string address = package;
                    string filename = Path.GetFileName(package);
                    string cabFile = Path.Combine(target, filename);
                    string ext = Path.GetExtension(package).ToUpper();

                    Console.WriteLine($"Downloading driver package for {TargetModel}...");

                    if (getFile(package, cabFile))
                    {
                        // Extract Destination
                        string dest = Path.Combine(TargetDevice, filename.Replace(ext, ""));
                        Directory.CreateDirectory(dest);

                        Console.WriteLine($"Extracting {ext} driver package to {dest}...");

                        string proc = string.Empty;
                        string param = string.Empty;
                        switch (ext)
                        {
                            case ".CAB":
                                proc = "EXPAND.EXE";
                                param = String.Format("{0} -F:* {1}", cabFile, dest);
                                break;

                            case ".EXE":
                                proc = cabFile;
                                param = "/s /e =" + dest;
                                break;

                            default: throw new ArgumentException("Unrecognized file type");
                        }

                        if (newProcess(proc, param).ExitCode != 0) throw new Exception();
                        DriverPath = dest;
                    }

                    else throw new Exception("Failed to download driver package!!");
                    break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message + "\r\n");
            }

            return;
        }

        public bool getFile(string address, string filename)
        {
            ///<synopsis>
            ///     Helper function for downloading files
            ///</synopsis>
            ///

            try
            {
                if (string.IsNullOrEmpty(address) ||
                    string.IsNullOrEmpty(filename)) throw new ArgumentNullException();

                if (!NetworkEnabled) throw new IOException("No Network Connection Found");
                if (new FileInfo(filename).Exists) return true;

                Uri uri = new Uri(address);
                WebClient client = new WebClient();
                System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12;
                client.DownloadProgressChanged += new DownloadProgressChangedEventHandler(inProgress);
                client.DownloadFileCompleted += new AsyncCompletedEventHandler(Completed);
                client.DownloadFileAsync(uri, filename);
                while (client.IsBusy) Thread.Sleep(1000);
                if (error) throw new Exception("Download FAILED");
                Console.WriteLine("\r\nDownload Completed\r\n");
                return true;
            }

            catch (Exception ex)
            {
                Console.WriteLine("\r\n" + ex.Message + "\r\n");
                return false;
            }

        }

        private void inProgress(object sender, DownloadProgressChangedEventArgs e)
        {
            double bytesTaken = e.BytesReceived;
            bytesTaken = Math.Round(bytesTaken, 4);
            double bytesTotal = e.TotalBytesToReceive;
            bytesTotal = Math.Round(bytesTotal, 4);

            Console.WriteLine("{0}    downloaded {1} of {2} MB. {3} % complete...",
            (string)e.UserState,
            bytesTaken / Math.Pow(1024, 2),
            bytesTotal / Math.Pow(1024, 2),
            e.ProgressPercentage);
        }

        private void Completed(object sender, AsyncCompletedEventArgs e)
        {
            if (e.Cancelled) error = true;
            Thread.Sleep(1000);
        }

        private Process newProcess(string filename, string args)
        {
            ///<synopsis>
            ///     Helper function for starting a new process.
            /// </synopsis>
            /// 

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

            ///  - Download Dell Driver Pack Catalog 
            ///  - Extract CAB file to TargetDevice

            error = false;
            string target = TargetDevice;

            try
            {
                string address = @"https://downloads.dell.com/catalog/DriverPackCatalog.cab";
                string filename = Path.GetFileName(address);
                string cabFile = Path.Combine(target, filename);

                Console.WriteLine("Downloading driver catalog...");

                if (getFile(address, cabFile))
                {
                    string xml = Path.Combine(target, "catalog.xml");
                    string str = String.Format("{0} -F:* {1}", cabFile, xml);
                    if (newProcess("expand.exe", str).ExitCode != 0) throw new Exception("Failed to extract Driver Catalog");
                    DriverCatalog = xml;
                }

                else throw new IOException("Failed to download DriverCatalog - " + address);
            }
            catch (Exception ex)
            {
                Console.WriteLine("\r\n" + ex.Message + "\r\n");
                error = true;
            }
        }

        public void GetPackage(string Model)
        {
            ///<synapsis>
            ///     Search XML document for Model, OS, Architecture 
            ///     Get Path To DriverPackage
            ///     Download Driver Package
            ///</synapsis>



            try
            {
                TargetModel = Model;
                if (string.IsNullOrEmpty(this.TargetModel)) throw new ArgumentNullException("TargetModel Is Not Set");
                if (string.IsNullOrEmpty(this.DriverCatalog)) getCatalog();

                TargetOS = "Windows10,x64";
                string os = TargetOS.Split(',')[0];
                string arc = TargetOS.Split(',')[1];

                while (!error)
                {
                    FileInfo catalog = new FileInfo(DriverCatalog);
                    if (!catalog.Exists) throw new FileNotFoundException("Catalog not found");

                    XmlDocument xDoc = new XmlDocument();
                    xDoc.Load(catalog.FullName);
                    var nsmgr = new XmlNamespaceManager(xDoc.NameTable);
                    nsmgr.AddNamespace("ns", "openmanage/cm/dm");

                    string pathModel = "//ns:DriverPackage/ns:SupportedSystems/ns:Brand/ns:Model[@name='" + TargetModel + "']";
                    string pathOS = "/ns:SupportedOperatingSystems/ns:OperatingSystem[@osCode='" + os + "' and @osArch='" + arc + "']";
                    string xPath = String.Format("{0}/../../..{1}/../..", pathModel, pathOS);
                    XmlNode TargetNode = xDoc.SelectNodes(xPath, nsmgr)[0];

                    if (null == TargetNode) throw new Exception("Package Not Found");

                    string Package = String.Join("/", "http://dl.dell.com", GetAttributeValue(TargetNode, "path"));
                    string Version = String.Join(",", GetAttributeValue(TargetNode, "vendorVersion"),
                                                      GetAttributeValue(TargetNode, "dellVersion"));
                    string Date = DateTime.Parse(GetAttributeValue(TargetNode, "dateTime")).ToString("MMMM dd, yyyy");

                    string results = String.Format(
                        "\r\nFound Driver Package - " +
                        "\r\n Model: {0} \r\n Operating System: {1} \r\n  Architecture: {2}" +
                        "\r\n Release: {3} \r\n Version: {4}\r\n Release Date: {5}\r\n",
                        TargetModel, os, arc, GetAttributeValue(TargetNode, "releaseID"), Version, Date
                        );

                    Console.WriteLine(results);
                    DriverPackage = Package;
                    break;
                }

                if (error) throw new Exception("Failed to find driver package in catalog");
            }

            catch (Exception ex)
            {
                error = true;
                Console.WriteLine("\r\n" + ex.Message + "\r\n");
                return;
            }
        }

        private string GetAttributeValue (XmlNode node, string Name)
        {
            ///<synopsis>
            ///     This helper function returns the Value of an attribute within an XML node.
            /// </synopsis>
                     
            string value = String.Empty;
            string NodeName = String.Empty;
            
            try
            {
                NodeName = node.Name;
                value = node.Attributes[Name].Value;
            }
            catch (Exception ex)
            {
                error = true;
                Console.WriteLine(String.Format(
                    "\r\nFailed to get '{0}' attribute value from XML Node '{1}'.\r\n{2}\r\n",
                     Name, NodeName, ex.Message));
            }

            return value;
        }
    }
}

