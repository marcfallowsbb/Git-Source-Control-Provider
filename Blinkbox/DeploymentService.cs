// --------------------------------------------------------------------------------------------------------------------
// <copyright file="DeploymentService.cs" company="blinkbox">
//   TODO: Update copyright text.
// </copyright>
// --------------------------------------------------------------------------------------------------------------------

namespace GitScc.Blinkbox
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Management.Automation;
    using System.Management.Automation.Runspaces;
    using System.Text.RegularExpressions;
    using System.Web;

    using GitScc.Blinkbox.Data;
    using GitScc.Blinkbox.Options;

    using Microsoft.Build.Evaluation;
    using Microsoft.Build.Execution;
    using Microsoft.Build.Framework;

    /// <summary>
    /// Performs deployments 
    /// </summary>
    public class DeploymentService : IDisposable
    {
        /// <summary>
        /// The current instance of the <see cref="SccProviderService"/>
        /// </summary>
        private readonly SccProviderService sccProviderService;

        /// <summary>
        /// Instance of the  <see cref="NotificationService"/>
        /// </summary>
        private readonly NotificationService notificationService;

        /// <summary>
        /// Initializes a new instance of the <see cref="DeploymentService"/> class.
        /// </summary>
        /// <param name="basicSccProvider">The basic SCC provider.</param>
        public DeploymentService(BasicSccProvider basicSccProvider)
        {
            this.sccProviderService = basicSccProvider.GetService<SccProviderService>();
            this.notificationService = basicSccProvider.GetService<NotificationService>();
        }

        /// <summary>
        /// Deploys using the deploy project specified in settings.
        /// </summary>
        /// <param name="deployment">The deployment.</param>
        /// <returns>true if the deploy was successful.</returns>
        public bool RunDeploy(Deployment deployment)
        {
            bool success;

            if (Path.GetExtension(SolutionSettings.Current.DeployProjectLocation) == ".ps1")
            {
                // Call a powershell script to run the deployment.
                var scriptName = SccHelperService.GetAbsolutePath(SolutionSettings.Current.DeployProjectLocation);

                var powershellArgs = string.Format(
                    "-buildProjectPath:'{0}' -buildLabel:'{1}' -branchName:'{2}' -release:'{3}'",
                    this.sccProviderService.GetSolutionFileName(),
                    deployment.BuildLabel,
                    SolutionSettings.Current.CurrentBranch,
                    SolutionSettings.Current.CurrentRelease);

                var powershellCall = string.Format(
                    "& '{0}' {1}",
                    scriptName,
                    powershellArgs);

                var command = new SccCommand("powershell.exe", powershellCall);
                command.Start();
                success = command.ExitCode == 0;
            }
            else
            {
                success = this.DeployMsBuild(deployment);
            }

            if (success)
            {
                // Save the deployment in userSettings. 
                var replacements = new Dictionary<string, string> 
                {
                    { "MachineName", Environment.MachineName },
                    { "BuildLabel", deployment.BuildLabel },
                    { "Tags", SolutionUserSettings.Current.TestSwarmTags },
                    { "RunnerMode", SolutionSettings.Current.TestRunnerMode },
                };
                deployment.AppUrl = SolutionUserSettings.Current.LocalAppUrlTemplate;
                deployment.TestRunUrl = SolutionUserSettings.Current.LocalTestUrlTemplate;

                foreach (var replacement in replacements)
                {
                    deployment.AppUrl = deployment.AppUrl.Replace("{" + replacement.Key + "}", replacement.Value);
                    deployment.TestRunUrl = deployment.TestRunUrl.Replace("{" + replacement.Key + "}", replacement.Value);
                }

                SolutionUserSettings.Current.LastDeployment = deployment;
                SolutionUserSettings.Current.Save();

                if (SolutionUserSettings.Current.SubmitTestsOnDeploy.GetValueOrDefault())
                {
                    // Submit tests to testswarm
                    this.SubmitTests();
                }
            }

            return success;
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
        }

        /// <summary>
        /// Submits a test run to testswarm.
        /// </summary>
        public void SubmitTests()
        {
            this.notificationService.ClearMessages();

            var scriptName = SccHelperService.GetAbsolutePath(SolutionSettings.Current.TestSubmissionScript);
            var featureDirectory = SccHelperService.GetAbsolutePath(SolutionSettings.Current.FeaturePath);

            var lastDeployment = SolutionUserSettings.Current.LastDeployment;
            if (lastDeployment == null)
            {
                NotificationService.DisplayError("cannot find previous deployment", "Please deploy first");
                return;
            }

            var args = new Dictionary<string, object>
                {
                    { "version", lastDeployment.BuildLabel },
                    { "featuresDirectory", featureDirectory },
                    { "branch", SolutionSettings.Current.CurrentBranch },
                    { "userName", SolutionUserSettings.Current.TestSwarmUsername },
                    { "password", SolutionUserSettings.Current.TestSwarmPassword },
                    { "appUrl", lastDeployment.AppUrl },
                    { "tag", SolutionUserSettings.Current.TestSwarmTags },
                    { "jobName", lastDeployment.Message + " (" + lastDeployment.BuildLabel + ")" }
                };

            RunPowershell(scriptName, args);
            /*
            var powershellArgs = string.Format(
                "-version:'{0}' -featuresDirectory:'{1}' -branch:'{2}' -userName:'{3}' -password:'{4}' -appUrl:'{5}' -tag:'{6}' -jobName:'{7}' ",
                lastDeployment.BuildLabel,
                featureDirectory,
                SolutionSettings.Current.CurrentBranch,
                SolutionUserSettings.Current.TestSwarmUsername,
                SolutionUserSettings.Current.TestSwarmPassword,
                lastDeployment.AppUrl,
                SolutionUserSettings.Current.TestSwarmTags,
                lastDeployment.Message + " (" + lastDeployment.Message + ")");

            var powershellCall = string.Format(
                "& '{0}' {1}",
                scriptName,
                powershellArgs);

            var command = new SccCommand("powershell.exe", powershellCall);
            command.Start();
             * */
        }

        /// <summary>
        /// Deploys using the deploy project specified in settings.
        /// </summary>
        /// <param name="deployment">The deployment.</param>
        /// <returns>true if the deploy was successful.</returns>
        private bool DeployMsBuild(Deployment deployment)
        {
            this.notificationService.AddMessage("Begin build and deploy to " + deployment.BuildLabel);

            // Look for a deploy project
            var buildProjectFileName = SccHelperService.GetAbsolutePath(SolutionSettings.Current.DeployProjectLocation);

            if (!File.Exists(buildProjectFileName))
            {
                NotificationService.DisplayError("Deploy abandoned", "build project not found");
                return false;
            }

            this.notificationService.AddMessage("Deploy project found at " + buildProjectFileName);

            // Initisalise our own project collection which can be cleaned up after the build. This is to prevent caching of the project. 
            using (var projectCollection = new ProjectCollection(Microsoft.Build.Evaluation.ProjectCollection.GlobalProjectCollection.ToolsetLocations))
            {
                var commitComment = Regex.Replace(deployment.Message, @"\r|\n|\t", string.Empty);
                commitComment = HttpUtility.UrlEncode(commitComment.Substring(0, commitComment.Length > 80 ? 80 : commitComment.Length));

                // Global properties need to be set before the projects are instantiated. 
                var globalProperties = new Dictionary<string, string>
                    {
                        { BlinkboxSccOptions.Current.CommitGuidPropertyName, deployment.BuildLabel }, 
                        { BlinkboxSccOptions.Current.CommitCommentPropertyName, commitComment }
                    };
                var msbuildProject = new ProjectInstance(buildProjectFileName, globalProperties, "4.0", projectCollection);

                // Build it
                this.notificationService.AddMessage("Building " + Path.GetFileNameWithoutExtension(msbuildProject.FullPath));
                var buildRequest = new BuildRequestData(msbuildProject, new string[] { });

                var buildParams = new BuildParameters(projectCollection);
                buildParams.Loggers = new List<ILogger> { new BuildNotificationLogger(this.notificationService) { Verbosity = LoggerVerbosity.Minimal } };

                var result = BuildManager.DefaultBuildManager.Build(buildParams, buildRequest);

                if (result.OverallResult == BuildResultCode.Failure)
                {
                    string message = result.Exception == null
                        ? "An error occurred during build; please see the pending changes window for details."
                        : result.Exception.Message;

                    NotificationService.DisplayError("Build failed", message);
                    return false;
                }

                var launchUrls = msbuildProject.Items.Where(pii => pii.ItemType == BlinkboxSccOptions.Current.UrlToLaunchPropertyName);

                if (UserSettings.Current.OpenUrlsAfterDeploy.GetValueOrDefault())
                {
                    // Launch urls in browser
                    this.notificationService.AddMessage("Launch urls...");
                    foreach (var launchItem in launchUrls)
                    {
                        BasicSccProvider.LaunchBrowser(launchItem.EvaluatedInclude);
                    }
                }

                // Clean up project to prevent caching.
                projectCollection.UnloadAllProjects();
                projectCollection.UnregisterAllLoggers();
            }

            return true;
        }

        private void RunPowershell(string script, IDictionary<string, object> parameters)
        {
            var host = new PowershellHost();
            try
            {
                using (var runSpace = RunspaceFactory.CreateRunspace(host))
                {
                    // Open the runspace.
                    runSpace.Open();

                    /* using (PowerShell powershell = PowerShell.Create())
                     {
                         powershell.AddScript(script);
                         foreach (var parameter in parameters)
                         {
                             powershell.AddParameter(parameter.Key, parameter.Value);
                         }
                         powershell.Runspace = runSpace;
                         var outputs = powershell.Invoke();

                     }*/

                    using (var pipeline = runSpace.CreatePipeline())
                    {
                        var scriptCommand = new Command(script, true);
                        pipeline.Commands.Add(scriptCommand);
                        pipeline.Commands[0].MergeMyResults(PipelineResultTypes.Error, PipelineResultTypes.Output); 
                        pipeline.Commands.Add("out-default"); 
                        ////pipeline.Output.DataReady += this.HandlePowershellOutput;
                        ////pipeline.Error.DataReady += this.HandlePowershellOutput;

                        foreach (var parameter in parameters)
                        {
                            scriptCommand.Parameters.Add(parameter.Key, parameter.Value);
                        }

                        // run script and get outputs
                        var outputObjects = pipeline.Invoke();

                        runSpace.SessionStateProxy.GetVariable("X-TestSwarm-JobId");
                    }
                }
            }
            catch (Exception ex)
            {
            }
        }

        /// <summary>
        /// Handles the powershell output.
        /// </summary>
        /// <param name="sender">The sender.</param>
        /// <param name="e">The <see cref="System.EventArgs"/> instance containing the event data.</param>
        private void HandlePowershellOutput(object sender, EventArgs e)
        {
            try
            {
                var reader = (PipelineReader<PSObject>)sender;
                if (reader != null)
                {
                    foreach (var output in reader.ReadToEnd())
                    {
                        notificationService.AddMessage(output.ToString());
                    }
                }
            }
            catch (Exception ex)
            {}
        }
    }
}