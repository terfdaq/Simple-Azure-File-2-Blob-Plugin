using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Microsoft.WindowsAzure;
using Microsoft.WindowsAzure.Diagnostics;
using Microsoft.WindowsAzure.ServiceRuntime;
using Microsoft.WindowsAzure.StorageClient;
using System.Security.AccessControl;
using System.Security.Principal;

namespace SimpleAzureFile2BlobPlugin
{
    public class FileSystemMonitor
    {
        private readonly static string _accountName = null, _accountKey = null, _containerName = null, _pathToMonitor = null;
        private readonly static CloudStorageAccount _storageAccount = null;
        private readonly static CloudBlobClient _blobClient = null;

        /// <summary>
        /// Static constructor to verify the path to monitor, set configuration settings and storage account
        /// </summary>
        static FileSystemMonitor()
        {
            try
            {
                // Gather the necessary variables
                if (RoleEnvironment.IsAvailable)
                {
                    _accountName = RoleEnvironment.GetConfigurationSettingValue("SimpleAzureFile2BlobPlugin.StorageAccountName");
                    _accountKey = RoleEnvironment.GetConfigurationSettingValue("SimpleAzureFile2BlobPlugin.StorageAccountPrimaryKey");
                    _containerName = RoleEnvironment.GetConfigurationSettingValue("SimpleAzureFile2BlobPlugin.SyncContainerName");

                    _pathToMonitor = Environment.GetEnvironmentVariable("RoleRoot") + @"\sitesroot\0";
                    if (!Directory.Exists(_pathToMonitor))
                    {
                        _pathToMonitor = Environment.GetEnvironmentVariable("RoleRoot") + @"\approot";
                    }

                    _pathToMonitor = Path.Combine(_pathToMonitor,
                        RoleEnvironment.GetConfigurationSettingValue("SimpleAzureFile2BlobPlugin.FolderToSync"));

                    Trace.TraceInformation(
                        "SimpleAzureFile2BlobPlugin::FileSystemMonitor Configuration Complete: Account Name = {0} Account Key = {1} Container Name = {2} Monitor Path = {3}",
                        _accountName, _accountKey, _containerName, _pathToMonitor);
                }
            }
            catch (Exception ex)
            {
                Trace.TraceError("!!!!SimpleAzureFile2BlobPlugin::FileSystemMonitor Failed to read configuration settings. Error: {0}", ex.Message);
            }

            if (!Directory.Exists(_pathToMonitor))
            {
                Trace.TraceError("!!!!SimpleAzureFile2BlobPlugin::FileSystemMonitor Error finding Monitor Path {0}", _pathToMonitor);
            }
            else
            {
                DirectorySecurity sec = Directory.GetAccessControl(_pathToMonitor);
                SecurityIdentifier everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
                sec.AddAccessRule(new FileSystemAccessRule(everyone,
                    FileSystemRights.Modify | FileSystemRights.Synchronize,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
                Directory.SetAccessControl(_pathToMonitor, sec);                            
            }

            try
            {
                if (string.IsNullOrEmpty(_accountKey) || string.IsNullOrEmpty(_accountName))
                    _storageAccount = CloudStorageAccount.DevelopmentStorageAccount;
                else
                    _storageAccount = new CloudStorageAccount(new StorageCredentialsAccountAndKey(_accountName, _accountKey), true);

                _blobClient = _storageAccount.CreateCloudBlobClient();
                _blobClient.GetContainerReference(_containerName).CreateIfNotExist();
            }
            catch (Exception ex)
            {
                Trace.TraceError("!!!!SimpleAzureFile2BlobPlugin::FileSystemMonitor Failed to set up the blob client. Error: {0}", ex.Message);
            }
        }

        /// <summary>
        /// Setup the different logging mechanisms
        /// </summary>
        private static void DiagnosticSetup()
        {
            DiagnosticMonitorConfiguration dmc = DiagnosticMonitor.GetDefaultInitialConfiguration();

            Trace.Listeners.Add(new Microsoft.WindowsAzure.Diagnostics.DiagnosticMonitorTraceListener());

            Trace.AutoFlush = true;

#if DEBUG
            var account = CloudStorageAccount.DevelopmentStorageAccount;
#else
            // USE CLOUD STORAGE    
            var account = CloudStorageAccount.Parse(RoleEnvironment.GetConfigurationSettingValue("Microsoft.WindowsAzure.Plugins.Diagnostics.ConnectionString"));
#endif

            // Start up the diagnostic manager with the given configuration
            DiagnosticMonitor.Start(account, dmc);
        }

        /// <summary>
        /// Determines if the given path is a directory.
        /// </summary>
        /// <param name="path">The path to check.</param>
        /// <returns>True if the path is a directory, false if it is a file.</returns>
        private static bool IsDirectory(string path)
        {
            FileAttributes attr = File.GetAttributes(path);
            return ((attr & FileAttributes.Directory) == FileAttributes.Directory);
        }

        #region Blob/File System Interaction

        private static void UploadBlob(string blobName)
        {
            Trace.TraceInformation("SimpleAzureFile2BlobPlugin::FileSystemMonitor Attempting to upload {0}", blobName);
            CloudBlob blob = null;
            try
            {
                // Only add the file to blob storage if it is a file. Directories dont need to be created
                // because the blob is just a flat list of files, and empty directories aren't useful.
                if (!IsDirectory(blobName))
                {
                    Trace.TraceInformation("SimpleAzureFile2BlobPlugin::FileSystemMonitor {0} is a file. Uploading!", blobName);
                    CloudBlobContainer container = _blobClient.GetContainerReference(_containerName);
                    blob = container.GetBlobReference(blobName.Replace(_pathToMonitor, "").TrimStart('\\'));
                    blob.UploadFile(blobName);
                    Trace.TraceInformation("SimpleAzureFile2BlobPlugin::FileSystemMonitor Blob uploaded to {0}", blob.Uri);
                }
            }
            catch (Exception ex)
            {
                Trace.TraceError("!!!!!!!!SimpleAzureFile2BlobPlugin::FileSystemMonitor Failed to upload blob {0}. Error: {1}", blob.Name, ex.Message);
            }
        }

        private static void DeleteBlob(string blobName)
        {
            Trace.TraceInformation("SimpleAzureFile2BlobPlugin::FileSystemMonitor Attempting to delete {0}", blobName);
            CloudBlob blob = null;
            try
            {
                Trace.TraceInformation("SimpleAzureFile2BlobPlugin::FileSystemMonitor Deleting {0}!", blobName);
                CloudBlobDirectory directory = _blobClient.GetBlobDirectoryReference(_containerName);
                blob = directory.GetBlobReference(blobName.Replace(_pathToMonitor, "").TrimStart('\\'));
                BlobRequestOptions blobRequestOptions = new BlobRequestOptions()
                {
                    DeleteSnapshotsOption = DeleteSnapshotsOption.IncludeSnapshots,
                    AccessCondition = AccessCondition.None
                };
                blob.DeleteIfExists(blobRequestOptions);
                Trace.TraceInformation("SimpleAzureFile2BlobPlugin::FileSystemMonitor Blob deleted");
            }
            catch (Exception ex)
            {
                Trace.TraceError("!!!!!!!!SimpleAzureFile2BlobPlugin::FileSystemMonitor Failed to delete blob {0}. Error: {1}", blob.Name, ex.Message);
            }
        }

        private static void DownloadBlob()
        {
            try
            {
                Trace.TraceInformation("SimpleAzureFile2BlobPlugin::FileSystemMonitor Attempting to download blobs.");

                CloudBlobDirectory directory = _blobClient.GetBlobDirectoryReference(_containerName);
                var blobs = directory.ListBlobs(new BlobRequestOptions()
                {
                    BlobListingDetails = BlobListingDetails.All,
                    UseFlatBlobListing = true
                });

                foreach (var blob in blobs)
                {
                    string localFilePath = blob.Uri.LocalPath.Replace(string.Format(@"/{0}", _containerName), _pathToMonitor).Replace('/', '\\');
                    if (!File.Exists(localFilePath) && !Directory.Exists(localFilePath))
                    {
                        string localFileDirectory =
                            string.IsNullOrWhiteSpace(Path.GetExtension(localFilePath)) ?
                            localFilePath :
                            Path.GetDirectoryName(localFilePath);

                        // Create the directory locally if it doesn't exist. Otherwise create the blob.
                        if (!Directory.Exists(localFileDirectory))
                            Directory.CreateDirectory(localFileDirectory);
                        else
                        {
                            string blobName = localFilePath.Replace(_pathToMonitor, "").TrimStart('\\').Replace("\\", "/");

                            Trace.TraceInformation("SimpleAzureFile2BlobPlugin::FileSystemMonitor Downloading blob {0} ", blobName);

                            CloudBlob cloudBlob = directory.GetBlobReference(blobName);
                            using (FileStream fs = new FileStream(localFilePath, FileMode.OpenOrCreate, FileAccess.Write))
                            {
                                cloudBlob.DownloadToStream(fs);
                                fs.Flush();
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.TraceError("!!!!!!!!SimpleAzureFile2BlobPlugin::FileSystemMonitor Failed to download blob. Error: {0}", ex.Message);
            }
        }

        #endregion Blob/File System Interaction

        /// <summary>
        /// Plugin starting point. Creates the watcher to monitor the files, and 
        /// runs a loop to keep the application running. This allows it to run in
        /// the background in the Azure Instance.
        /// </summary>
        /// <param name="args"></param>
        static void Main(string[] args)
        {
            Trace.TraceInformation("SimpleAzureFile2BlobPlugin::FileSystemMonitor Started");
            
            DiagnosticSetup();

            CloudStorageAccount.SetConfigurationSettingPublisher((configName, configSettingPublisher) =>
            {
                var connectionString = RoleEnvironment.GetConfigurationSettingValue(configName);
                configSettingPublisher(connectionString);
            });
            
            // When the process starts we need to download all the blobs from storage to make sure we are n' sync.
            DownloadBlob();

            using (FileSystemWatcher watcher = new FileSystemWatcher()
            {
                Filter = "*.*",
                Path = _pathToMonitor,
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastAccess | NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
                EnableRaisingEvents = true,
                InternalBufferSize = 64 * 1024
            })
            {
                watcher.Changed += watcher_Changed;
                watcher.Created += watcher_Created;
                watcher.Deleted += watcher_Deleted;
                watcher.Renamed += watcher_Renamed;
                watcher.Error += watcher_Error;
                while (true)
                {
                    Thread.Sleep(1000);
                }
            }
        }
        
        static void watcher_Created(object sender, FileSystemEventArgs e)
        {
            Trace.TraceInformation("SimpleAzureFile2BlobPlugin::FileSystemMonitor Created {0}", e.FullPath);
            UploadBlob(e.FullPath);
        }
        static void watcher_Deleted(object sender, FileSystemEventArgs e)
        {
            Trace.TraceInformation("SimpleAzureFile2BlobPlugin::FileSystemMonitor Deleted {0}", e.FullPath);
            DeleteBlob(e.FullPath);
        }
        static void watcher_Renamed(object sender, RenamedEventArgs e)
        {
            Trace.TraceInformation("SimpleAzureFile2BlobPlugin::FileSystemMonitor Renamed {0} to {1}", e.OldFullPath, e.FullPath);
            UploadBlob(e.FullPath);
            DeleteBlob(e.OldFullPath);
        }
        static void watcher_Changed(object sender, FileSystemEventArgs e)
        {
            Trace.TraceInformation("SimpleAzureFile2BlobPlugin::FileSystemMonitor Changed {0}", e.FullPath);
            UploadBlob(e.FullPath);
        }
        static void watcher_Error(object sender, ErrorEventArgs e)
        {
            Trace.TraceError("!!!!!!!!SimpleAzureFile2BlobPlugin::FileSystemMonitor Watcher Error: {0}", e.GetException().Message);
        }
    }
}
