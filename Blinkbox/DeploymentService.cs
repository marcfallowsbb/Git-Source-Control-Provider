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
    using System.Net;
    using System.Text;
    using System.Text.RegularExpressions;
    using System.Web;

    using GitScc.Blinkbox.Data;
    using GitScc.Blinkbox.Options;
    using GitScc.Blinkbox.UI;

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
        private readonly BasicSccProvider basicSccProvider;

        /// <summary>
        /// The current instance of the <see cref="SccProviderService"/>
        /// </summary>
        private readonly SccProviderService sccProviderService;

        /// <summary>
        /// Instance of the  <see cref="NotificationService"/>
        /// </summary>
        private readonly NotificationService notificationService;

        /// <summary>
        /// List the deployments made
        /// </summary>
        private List<Deployment> deployments = new List<Deployment>();

        public List<Deployment> Deployments { get; private set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="DeploymentService"/> class.
        /// </summary>
        /// <param name="basicSccProvider">The basic SCC provider.</param>
        public DeploymentService(BasicSccProvider basicSccProvider)
        {
            this.basicSccProvider = basicSccProvider;
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
            this.notificationService.AddMessage("Begin build and deploy to " + deployment.Version);

            // Look for a deploy project
            var buildProjectFileName = Path.IsPathRooted(SolutionSettings.Current.DeployProjectLocation)
                    ? SolutionSettings.Current.DeployProjectLocation
                    : Path.Combine(this.sccProviderService.GetSolutionDirectory(), SolutionSettings.Current.DeployProjectLocation);

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
                        { BlinkboxSccOptions.Current.CommitGuidPropertyName, deployment.Version }, 
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

                if (SolutionUserSettings.Current.SubmitTestsOnDeploy.GetValueOrDefault())
                {
                    // Submit tests to testswarm
                    this.SubmitTests();
                }

                var launchUrls = msbuildProject.Items.Where(pii => pii.ItemType == BlinkboxSccOptions.Current.UrlToLaunchPropertyName);
                deployment.AppUrl = msbuildProject.Properties.FirstOrDefault(p => p.Name == "LaunchAppUrl").EvaluatedValue;
                deployment.TestRunUrl = msbuildProject.Properties.FirstOrDefault(p => p.Name == "LaunchTestRunUrl").EvaluatedValue;

                if (UserSettings.Current.OpenUrlsAfterDeploy.GetValueOrDefault())
                {
                    // Launch urls in browser
                    this.notificationService.AddMessage("Launch urls...");
                    foreach (var launchItem in launchUrls)
                    {
                        BasicSccProvider.LaunchBrowser(launchItem.EvaluatedInclude);
                    }
                }

                this.deployments.Add(deployment);

                // Clean up project to prevent caching.
                projectCollection.UnloadAllProjects();
                projectCollection.UnregisterAllLoggers();

                try
                {
                    var deployTab = BasicSccProvider.GetServiceEx<deployTab>();
                    deployTab.LastDeployment = deployment;
                    deployTab.RefreshBindings();
                }
                catch
                { }
            }

            return true;
        }

        /// <summary>
        /// Submits a test run to testswarm.
        /// </summary>
        /// <returns>the jobID asw a string</returns>
        public string SubmitTests()
        {
            var sccHelperService = BasicSccProvider.GetServiceEx<SccHelperService>();
            var form = new StringBuilder();
            var tag = SolutionUserSettings.Current.TestSwarmTags;
            var featurePath = SolutionSettings.Current.FeaturePath.StartsWith("\\")
                ? this.sccProviderService.GetSolutionDirectory().TrimEnd("\\".ToCharArray()) + SolutionSettings.Current.FeaturePath
                : SolutionSettings.Current.FeaturePath;
            var testSwarmUrl = SolutionSettings.Current.TestSwarmUrl.TrimEnd("/".ToCharArray());
            var testUrl = string.Format(
                "http://tv-{0}.bbdev1.com/Client/V2-dev-{1}/Test/Index.html?tags={2}&runnermode={3}", 
                Environment.MachineName,
                sccHelperService.GetHeadRevisionHash(),
                tag.ToLower(),
                SolutionSettings.Current.TestRunnerMode.ToLower());

            // get all feature files containing the specified tag.
            var featureFiles = Directory.GetFiles(featurePath, "*.feature", SearchOption.AllDirectories).AsEnumerable();
            if (!string.IsNullOrEmpty(tag))
            {
                featureFiles = featureFiles.Where(f => File.ReadAllText(f).Contains(tag));
            }

            // Build up the form
            form.Append(string.Format(
                "jobName={0} ({1})&runMax={2}", 
                sccHelperService.GetLastCommitMessage(),
                sccHelperService.GetHeadRevisionHash(),
                3));

            foreach (var browserSet in SolutionSettings.Current.TestBrowserSets.Split(new[] {","}, StringSplitOptions.RemoveEmptyEntries))
            {
                form.Append("&browserSets[]=").Append(browserSet.ToLower());
            }

            foreach (var file in featureFiles)
            {
                var featureName = file.Replace(featurePath, string.Empty);
                form.Append("&runNames[]=").Append(featureName);

                // NOTE: capitalisation of pfeaturePath is important. 
                form.Append("&runUrls[]=").Append(System.Web.HttpUtility.UrlEncode(testUrl + "&featurePath=" + featureName));
            }

            // Login to testswarm
            this.notificationService.AddMessage("Login to " + testSwarmUrl + "/login");

            var login = this.Request(testSwarmUrl + "/login", string.Format("username={0}&password={1}", SolutionUserSettings.Current.TestSwarmUsername, SolutionUserSettings.Current.TestSwarmPassword));
            var authCookie = login.Headers["Set-Cookie"];
            if (string.IsNullOrEmpty(authCookie))
            {
                this.notificationService.AddMessage("Login failed");
            }


            // Submit the job.
            this.notificationService.AddMessage("Submit job");

            var job = this.Request(testSwarmUrl + "/adddevboxjob", form.ToString(), authCookie);
            if (job.StatusCode == HttpStatusCode.OK)
            {
                var jobId = job.Headers["X-TestSwarm-JobId"];
                this.notificationService.AddMessage("jobID = " + jobId);
            }
            else
            {
                NotificationService.DisplayError("Testswarm submit failed", "Testswarm submit failed");
            }

            return null;
        }


        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {

        }

        /// <summary>
        /// Requests the specified URL.
        /// </summary>
        /// <param name="url">The URL.</param>
        /// <param name="content">The content.</param>
        /// <param name="authCookie">The auth cookie.</param>
        /// <returns></returns>
        private System.Net.HttpWebResponse Request(string url, string content, string authCookie = null)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.ContentType = "application/x-www-form-urlencoded";
            request.Method = "POST";
            request.UserAgent = "Mozilla/5.0 (Windows NT 6.1; WOW64) AppleWebKit/537.1 (KHTML, like Gecko) Chrome/21.0.1180.89 Safari/537.1";
            request.AllowAutoRedirect = false;

            if (!string.IsNullOrEmpty(authCookie))
            {
                request.Headers.Add("Cookie", authCookie); 
            }

            var contentBytes = System.Text.Encoding.UTF8.GetBytes(content);
            request.ContentLength = contentBytes.Length;
            var requestStream = request.GetRequestStream();
            requestStream.Write(contentBytes, 0, contentBytes.Length);
            requestStream.Close();

            var response = request.GetResponse();
            return (HttpWebResponse)response;
        }
    }
}