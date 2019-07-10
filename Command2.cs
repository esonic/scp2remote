using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.IO;
using System.Threading;
using EnvDTE;
using liblinux;
using liblinux.IO;
using liblinux.Persistence;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace VSIXScp
{
    /// <summary>
    /// Command handler
    /// </summary>
    internal sealed class Command2
    {
        /// <summary>
        /// Command ID.
        /// </summary>
        public const int CommandId = 0x0100;

        /// <summary>
        /// Command menu group (command set GUID).
        /// </summary>
        public static readonly Guid CommandSet = new Guid("1ea07397-cdf4-4d8b-970d-0ccae94e0fdc");

        /// <summary>
        /// VS Package that provides this command, not null.
        /// </summary>
        private readonly AsyncPackage package;

        private object lock_obj = new object();
        private RemoteSystem remoteSystem;
        private IRemoteDirectory directory;

        /// <summary>
        /// Initializes a new instance of the <see cref="Command2"/> class.
        /// Adds our command handlers for menu (commands must exist in the command table file)
        /// </summary>
        /// <param name="package">Owner package, not null.</param>
        /// <param name="commandService">Command service to add command to, not null.</param>
        private Command2(AsyncPackage package, OleMenuCommandService commandService)
        {
            this.package = package ?? throw new ArgumentNullException(nameof(package));
            commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));

            var menuCommandID = new CommandID(CommandSet, CommandId);
            var menuItem = new MenuCommand(new EventHandler(this.ExecuteAsync), menuCommandID);
            commandService.AddCommand(menuItem);

            try
            {
                ConnectionInfoStore connectionInfoStore = new ConnectionInfoStore();
                connectionInfoStore.Load();
                if (connectionInfoStore.Connections.Count > 0)
                {
                    remoteSystem = new RemoteSystem((ConnectionInfo)connectionInfoStore.Connections[0]);
                    directory = remoteSystem.FileSystem.GetDirectory(SpecialDirectory.Home);
                }
            }
            catch { }
        }

        /// <summary>
        /// Gets the instance of the command.
        /// </summary>
        public static Command2 Instance
        {
            get;
            private set;
        }

        /// <summary>
        /// Gets the service provider from the owner package.
        /// </summary>
        private Microsoft.VisualStudio.Shell.IAsyncServiceProvider ServiceProvider
        {
            get
            {
                return this.package;
            }
        }

        /// <summary>
        /// Initializes the singleton instance of the command.
        /// </summary>
        /// <param name="package">Owner package, not null.</param>
        public static async Task InitializeAsync(AsyncPackage package)
        {
            // Switch to the main thread - the call to AddCommand in Command2's constructor requires
            // the UI thread.
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            OleMenuCommandService commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            Instance = new Command2(package, commandService);
        }

        /// <summary>
        /// This function is the callback used to execute the command when the menu item is clicked.
        /// See the constructor to see how the menu item is associated with this function using
        /// OleMenuCommandService service and MenuCommand class.
        /// </summary>
        /// <param name="sender">Event sender.</param>
        /// <param name="e">Event args.</param>
        private async void ExecuteAsync(object sender, EventArgs e)
        {
            await RunCopyAsync();
        }

        private Task RunCopyAsync()
        {
            return Task.Run(() =>
            {
                DoCopy();
            });
        }

        private void DoCopy()
        {
            try
            {
                var dte = Package.GetGlobalService(typeof(DTE)) as EnvDTE80.DTE2;
                // var dte = ServiceProvider.GetServiceAsync(typeof(EnvDTE.DTE)).Result as EnvDTE80.DTE2;
                Scp(dte);
            }
            catch (Exception ex)
            {
                VsShellUtilities.ShowMessageBox(
                                this.package,
                                ex.ToString() + "\r\n" + ex.StackTrace.ToString(),
                                "Scp failed",
                                OLEMSGICON.OLEMSGICON_WARNING,
                                OLEMSGBUTTON.OLEMSGBUTTON_OK,
                                OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
            }
        }

        public void ScpToRemote(List<string> files, string dir_path, string dir, IVsOutputWindowPane outputWindowPane)
        {
            try
            {
                lock (lock_obj)
                {
                    // ThreadHelper.ThrowIfNotOnUIThread();
                    var dte = Package.GetGlobalService(typeof(DTE)) as EnvDTE80.DTE2;
                    dte.Windows.Item(EnvDTE.Constants.vsWindowKindOutput).Activate();
                    if (files.Count == 0)
                    {
                        outputWindowPane.OutputStringThreadSafe("No file can be copied");
                        return;
                    }
                    ConnectionInfoStore connectionInfoStore = new ConnectionInfoStore();
                    connectionInfoStore.Load();
                    if (connectionInfoStore.Connections.Count < 1)
                    {
                        outputWindowPane.OutputStringThreadSafe("No connection found. Add connection in [Tools] / [Options] / [Cross Platform]");
                        return;
                    }
                    outputWindowPane.OutputStringThreadSafe("Connecting...\n");
                    if (remoteSystem == null)
                    {
                        remoteSystem = new RemoteSystem((ConnectionInfo)connectionInfoStore.Connections[0]);
                        directory = remoteSystem.FileSystem.GetDirectory(SpecialDirectory.Home);
                    }
                    using (var concurrencySemaphore = new System.Threading.SemaphoreSlim(8))
                    {
                        int num = 0;
                        int length = files.Count;
                        List<Task> tasks = new List<Task>();
                        foreach (string file in files)
                        {
                            concurrencySemaphore.Wait();
                            var t = Task.Run(() =>
                            {
                                try
                                {
                                    string str2 = file.Substring(dir_path.Length);
                                    string remoteFileName = directory.FullPath + "/projects/" + dir + str2.Replace('\\', '/');
                                    string remotePath = remoteFileName.Substring(0, remoteFileName.LastIndexOf('/'));
                                    if (File.Exists(file))
                                    {
                                        if (!remoteSystem.FileSystem.Exists(remotePath))
                                            remoteSystem.FileSystem.CreateDirectories(remotePath);
                                        remoteSystem.FileSystem.UploadFile(file, remoteFileName);
                                        outputWindowPane.OutputStringThreadSafe("[" + Interlocked.Increment(ref num) + "/" + length + "] " + remoteFileName + "\n");
                                    }
                                    else
                                    {
                                        Interlocked.Increment(ref num);
                                        outputWindowPane.OutputStringThreadSafe("Skip " + file + " (file not exists)\n");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Interlocked.Increment(ref num);
                                    outputWindowPane.OutputStringThreadSafe("Upload failed: " + file + "\n");
                                }
                                finally
                                {
                                    concurrencySemaphore.Release();
                                }
                            });
                            tasks.Add(t);
                        }
                        Task.WaitAll(tasks.ToArray());
                    }
                    remoteSystem.Disconnect();
                    remoteSystem.Dispose();
                    outputWindowPane.OutputStringThreadSafe("Copy to " + remoteSystem.ConnectionInfo.HostNameOrAddress + " done.\n");
                    // prepare for next time
                    remoteSystem = new RemoteSystem((ConnectionInfo)connectionInfoStore.Connections[0]);
                    directory = remoteSystem.FileSystem.GetDirectory(SpecialDirectory.Home);
                }
            }
            catch (Exception ex)
            {
                remoteSystem = null;
                throw ex;
            }
        }

        private void Scp(EnvDTE80.DTE2 dte)
        {
            // ThreadHelper.ThrowIfNotOnUIThread();
            Array selectedItems = dte.ToolWindows.SolutionExplorer.SelectedItems as Array;
            if (selectedItems == null)
                return;

            IVsOutputWindow outWindow = Package.GetGlobalService(typeof(SVsOutputWindow)) as IVsOutputWindow;

            Guid generalPaneGuid = Microsoft.VisualStudio.VSConstants.GUID_OutWindowGeneralPane;
            IVsOutputWindowPane outputWindowPane;
            outWindow.GetPane(ref generalPaneGuid, out outputWindowPane);
            if (outputWindowPane == null)
            {
                outWindow.CreatePane(ref generalPaneGuid, "Copy To Remote", 1, 1);
                outWindow.GetPane(ref generalPaneGuid, out outputWindowPane);
            }

            outputWindowPane.Clear();
            outputWindowPane.Activate();
            // dte.Windows.Item("{34E76E81-EE4A-11D0-AE2E-00A0C90FFFC3}").Activate();

            Array solutionProjects = dte.ActiveSolutionProjects as Array;
            if (solutionProjects.Length < 1)
            {
                outputWindowPane.OutputStringThreadSafe("No active project.");
                return;
            }
            Project project = solutionProjects.GetValue(0) as Project;
            if (project == null)
            {
                outputWindowPane.OutputStringThreadSafe("Invalid active project type.");
                return;
            }

            List<string> files = new List<string>();
            foreach (UIHierarchyItem uiHierarchyItem in selectedItems)
            {
                ProjectItem projectItem = uiHierarchyItem.Object as ProjectItem;
                if (projectItem == null)
                {
                    outputWindowPane.OutputStringThreadSafe("Skip " + uiHierarchyItem.Name + " (not a project item)\n");
                }
                else
                {
                    string file = projectItem.Properties.Item("FullPath").Value.ToString();
                    if (File.Exists(file))
                    {
                        files.Add(file);
                    }
                    else
                    {
                        outputWindowPane.OutputStringThreadSafe("Skip " + uiHierarchyItem.Name + " (file not exists)\n");
                    }
                }
            }

            string directoryName = Path.GetDirectoryName(project.FullName);
            ScpToRemote(files, directoryName, project.Name, outputWindowPane);
        }
    }
}
