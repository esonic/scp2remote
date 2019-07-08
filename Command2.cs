using System;
using System.ComponentModel.Design;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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

        private static void Scp(EnvDTE80.DTE2 dte)
        {
            //ThreadHelper.ThrowIfNotOnUIThread();
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
            string directoryName = Path.GetDirectoryName(project.FullName);
            ConnectionInfoStore connectionInfoStore = new ConnectionInfoStore();
            connectionInfoStore.Load();
            if (connectionInfoStore.Connections.Count < 1)
            {
                outputWindowPane.OutputStringThreadSafe("No connection found. Add connection in [Tools] / [Options] / [Cross Platform]");
                return;
            }
            outputWindowPane.OutputStringThreadSafe("Connecting...\n");
            dte.Windows.Item("{34E76E81-EE4A-11D0-AE2E-00A0C90FFFC3}").Activate();
            RemoteSystem remoteSystem = new RemoteSystem((ConnectionInfo)connectionInfoStore.Connections[0]);
            IRemoteDirectory directory = remoteSystem.FileSystem.GetDirectory(SpecialDirectory.Home);
            int num = 1;
            int length = selectedItems.Length;
            foreach (UIHierarchyItem uiHierarchyItem in selectedItems)
            {
                ProjectItem projectItem = uiHierarchyItem.Object as ProjectItem;
                if (projectItem == null)
                {
                    ++num;
                    outputWindowPane.OutputStringThreadSafe("Skip " + uiHierarchyItem.Name + " (not a project item)\n");
                }
                else
                {
                    string str1 = projectItem.Properties.Item((object)"FullPath").Value.ToString();
                    string str2 = str1.Substring(directoryName.Length);
                    string remoteFileName = directory.FullPath + "/projects/" + project.Name + str2.Replace('\\', '/');
                    string remotePath = remoteFileName.Substring(0, remoteFileName.LastIndexOf('/'));
                    if (File.Exists(str1))
                    {
                        if (!remoteSystem.FileSystem.Exists(remotePath))
                            remoteSystem.FileSystem.CreateDirectories(remotePath);
                        remoteSystem.FileSystem.UploadFile(str1, remoteFileName);
                        outputWindowPane.OutputStringThreadSafe("[" + (object)num++ + "/" + (object)length + "] " + remoteFileName + "\n");
                    }
                    else
                    {
                        ++num;
                        outputWindowPane.OutputStringThreadSafe("Skip " + uiHierarchyItem.Name + " (file not exists)\n");
                    }
                }
            }
            remoteSystem.Disconnect();
            remoteSystem.Dispose();
            outputWindowPane.OutputStringThreadSafe("Copy to " + remoteSystem.ConnectionInfo.HostNameOrAddress + " done.\n");
        }
    }
}
