using System;
using System.ComponentModel.Design;
using System.Globalization;
using System.Collections.Generic;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace VSIXScp
{
    /// <summary>
    /// Command handler
    /// </summary>
    internal sealed class Command1
    {
        /// <summary>
        /// Command ID.
        /// </summary>
        public const int CommandId = 256;

        /// <summary>
        /// Command menu group (command set GUID).
        /// </summary>
        public static readonly Guid CommandSet = new Guid("9d77ce50-c534-4823-bb66-6ec41c40f9aa");

        /// <summary>
        /// VS Package that provides this command, not null.
        /// </summary>
        private readonly AsyncPackage package;
        private Microsoft.TeamFoundation.Controls.ITeamExplorer teamExplorer;
        private Microsoft.TeamFoundation.Controls.ITeamExplorerPage teamExplorerPage;

        /// <summary>
        /// Initializes a new instance of the <see cref="Command1"/> class.
        /// Adds our command handlers for menu (commands must exist in the command table file)
        /// </summary>
        /// <param name="package">Owner package, not null.</param>
        /// <param name="commandService">Command service to add command to, not null.</param>
        private Command1(AsyncPackage package, OleMenuCommandService commandService, Microsoft.TeamFoundation.Controls.ITeamExplorer exp)
        {
            this.package = package ?? throw new ArgumentNullException(nameof(package));
            commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));
            this.teamExplorer = exp ?? throw new ArgumentNullException(nameof(exp));

            var menuCommandID = new CommandID(CommandSet, CommandId);
            var menuItem = new MenuCommand(new EventHandler(this.ExecuteAsync), menuCommandID);
            commandService.AddCommand(menuItem);
        }

        /// <summary>
        /// Gets the instance of the command.
        /// </summary>
        public static Command1 Instance
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
            // Switch to the main thread - the call to AddCommand in Command1's constructor requires
            // the UI thread.
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            OleMenuCommandService commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            var exp = await package.GetServiceAsync(typeof(Microsoft.TeamFoundation.Controls.ITeamExplorer))
               as Microsoft.TeamFoundation.Controls.ITeamExplorer;
            Instance = new Command1(package, commandService, exp);
        }

        private static string FindGitPath(string file)
        {
            try
            {
                System.IO.DirectoryInfo parent = System.IO.Directory.GetParent(file);
                while (parent != null)
                {
                    if (System.IO.Directory.Exists(parent.FullName + "\\.git"))
                    {
                        return parent.FullName;
                    }
                    parent = parent.Parent;
                }
            }
            catch
            {
            }
            return null;
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
            try
            {
                Microsoft.TeamFoundation.Git.Controls.Extensibility.IChangesExt changesExt;

                int times = 20;
                while (teamExplorerPage == null && times-- > 0)
                {
                    await Task.Run(() =>
                    {
                        System.Threading.Thread.Sleep(500);
                    });
                    teamExplorerPage = teamExplorer.NavigateToPage(new Guid(Microsoft.TeamFoundation.Controls.TeamExplorerPageIds.GitChanges), null);
                }
                if (teamExplorerPage == null)
                {
                    throw new ArgumentNullException("TeamExplorerPage", "TeamExplorerPage is null");
                }

                times = 20;
                while (teamExplorerPage.IsBusy && times-- > 0)
                {
                    await Task.Run(() =>
                    {
                        System.Threading.Thread.Sleep(500);
                    });
                }

                changesExt = teamExplorerPage.GetExtensibilityService(typeof(Microsoft.TeamFoundation.Git.Controls.Extensibility.IChangesExt))
                   as Microsoft.TeamFoundation.Git.Controls.Extensibility.IChangesExt;
                times = 20;
                while (changesExt == null && times-- > 0)
                {
                    await Task.Run(() =>
                    {
                        System.Threading.Thread.Sleep(500);
                    });
                    changesExt = teamExplorerPage.GetExtensibilityService(typeof(Microsoft.TeamFoundation.Git.Controls.Extensibility.IChangesExt))
                   as Microsoft.TeamFoundation.Git.Controls.Extensibility.IChangesExt;
                }
                if (changesExt == null)
                {
                    throw new ArgumentNullException("ChangesExt", "ChangesExt is null");
                }
                times = 10;
                while (changesExt.IncludedChanges.Count == 0 && times-- > 0)
                {
                    await Task.Run(() =>
                    {
                        System.Threading.Thread.Sleep(500);
                    });
                }

                List<string> files = new List<string>();
                foreach (Microsoft.TeamFoundation.Git.Controls.Extensibility.IChangesPendingChangeItem change in changesExt.IncludedChanges)
                {
                    files.Add(change.SourceLocalItem);
                }

                //var dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE80.DTE2;
                //dte.Windows.Item(EnvDTE.Constants.vsWindowKindSolutionExplorer).Activate();
                await RunCopyAsync(files);
            }
            catch (Exception ex)
            {
                teamExplorerPage = null;
                VsShellUtilities.ShowMessageBox(
                                this.package,
                                ex.ToString() + "\r\n" + ex.StackTrace.ToString(),
                                "Scp failed",
                                OLEMSGICON.OLEMSGICON_WARNING,
                                OLEMSGBUTTON.OLEMSGBUTTON_OK,
                                OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
            }
        }

        private Task RunCopyAsync(List<string> files)
        {
            return Task.Run(() =>
            {
                Execute(files);
            });
        }

        private void Execute(List<string> files)
        {
            try
            {
                // ThreadHelper.ThrowIfNotOnUIThread();
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

                string git_path = null, dir = null;
                if (files.Count > 0)
                {
                    git_path = FindGitPath(files[0]);
                    var path = new System.IO.DirectoryInfo(git_path);
                    dir = path.Name;
                }
                Command2.Instance.ScpToRemote(files, git_path, dir, outputWindowPane);
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
    }
}
