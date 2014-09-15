using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AlcatelCtiInterface;
using AlcatelXmlApi6;

namespace MassiveParallelizationTest
{
    public class Tester
    {

        AlcatelXmlApi6.XMLApiConnector connector;
        protected string ServerURL { get; set; }
        ParallelOptions pOptions;
        public int LogLevel { get; set; }
        int minNbThreads, maxNbThreads, minIoThreads, maxIoThreads;
        object threadPoolLock = new object();

        public Tester()
        {
            AlcatelXmlApi6.XmlServerConfiguration config = new AlcatelXmlApi6.XmlServerConfiguration
            {
                ApiVersion = "6.0",
                LogAction = log,
                Login = "sdkuser",
                Password = "sdkuser",
                SessionRefreshInterval = 2000,
                UseHttp = false,
                ServerName = "p-icsal-as-01.swi.srse.net"
            };
            config.ServerName = "chdevics65.nxodev.intra";
            connector = new AlcatelXmlApi6.XMLApiConnector(config);
            
            ServerURL = connector.ServerURL;
            ServicePointManager.CheckCertificateRevocationList = false;
            ServicePointManager.DefaultConnectionLimit = 10;
            ServicePointManager.UseNagleAlgorithm = false;
            LogLevel = 4;
            pOptions = new ParallelOptions { MaxDegreeOfParallelism = -1 };
            log("Connection limit: " + ServicePointManager.DefaultConnectionLimit + ", max degree of parallelism: " + pOptions.MaxDegreeOfParallelism, 4);
            dumpThreadPoolConfig();
        }

        private void dumpThreadPoolConfig()
        {
            int minNbThreads, maxNbThreads, minIoThreads, maxIoThreads;
            ThreadPool.GetMaxThreads(out maxNbThreads, out maxIoThreads);
            ThreadPool.GetMinThreads(out minNbThreads, out minIoThreads);
            lock (threadPoolLock)
            {
                if (minIoThreads != this.minIoThreads || minNbThreads != this.minNbThreads || maxNbThreads != this.maxNbThreads || maxIoThreads != this.maxIoThreads)
                {
                    log("Threadpool config: min: " + minNbThreads + ", max: " + maxNbThreads + ", min io: " + minIoThreads + ", max io: " + maxIoThreads, 4);
                    this.minIoThreads = minIoThreads;
                    this.minNbThreads = minNbThreads;
                    this.maxIoThreads = maxIoThreads;
                    this.maxNbThreads = maxNbThreads;
                }
            }
        }

        public bool Init()
        {
            GenericOperationResult initRes = connector.Init(true, new List<AlcatelXmlApi6.ApiType> 
            { 
                AlcatelXmlApi6.ApiType.FrameworkManagement, 
                AlcatelXmlApi6.ApiType.Phone 
            });
            if (initRes.Success)
            {
                ApiFrameworkLoginResult loginRes = connector.ApiLogin();
                return loginRes.Success;
            }
            return initRes.Success;
        }

        public async Task<List<string>> GetUserList()
        {
            try
            {
                AlcatelCtiApiUserResult<List<string>> userIds = await connector.ExtractUsers().ConfigureAwait(false);
                return userIds.ResultObject;
            }
            catch (Exception e)
            {
                log("Unable to get user list: " + e.Message, 2);
            }
            return null;
        }

        public void LoadUsersInParallel(List<string> userIds)
        {
            bool retval = true;
            ConcurrentBag<string> usersFailedToLoad = new ConcurrentBag<string>();
            DateTime start = DateTime.Now;
            try
            {
                Parallel.ForEach(userIds, pOptions,(userId) => 
                {
                    retval &= loadUser(userId, usersFailedToLoad);
                });
            }
            catch (AggregateException aex)
            {
                processException(aex);
                retval = false;
            }
            TimeSpan duration = DateTime.Now.Subtract(start);
            log("Duration for parallel sync user loading: " + duration.TotalMilliseconds, 4);
            log("Result of framework user loading: " + retval + " nb failed " + usersFailedToLoad.Count, 4);
        }

        public async Task LoadUsersSequentialAsync(List<string> userIds)
        {
            bool retval = true;
            ConcurrentBag<string> usersFailedToLoad = new ConcurrentBag<string>();
            List<Task<AlcFrameworkUserOperationResult<AlcatelXmlApi6.AlcFwManagement.AlcFrameworkUser>>> userLoadTasks = new 
                List<Task<AlcFrameworkUserOperationResult<AlcatelXmlApi6.AlcFwManagement.AlcFrameworkUser>>>();
            Dictionary<int, string> loadTaskIds = new Dictionary<int, string>();
            DateTime start = DateTime.Now;
            foreach (string userId in userIds)
            {
                Task<AlcFrameworkUserOperationResult<AlcatelXmlApi6.AlcFwManagement.AlcFrameworkUser>> loadTask = connector.GetFrameworkUserAsync(userId, true);
                userLoadTasks.Add(loadTask);
                loadTaskIds.Add(loadTask.Id, userId);
            }
            try
            {
                await Task.WhenAll(userLoadTasks).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                int nbCompleted = userLoadTasks.Where(t => t.IsCompleted).Count();
                int nbFaulted = userLoadTasks.Where(t => t.IsFaulted).Count();
                log("Exception when loading framework users from server " + ServerURL + ": " + e.Message + ", nb completed " + nbCompleted + " nb faulted "
                    + nbFaulted, 2);
                StringBuilder sb = new StringBuilder();
                if (nbFaulted > 0)
                    sb.Append("Dumping errors of faulted tasks: ");
                foreach (Task t in userLoadTasks.Where(t => t.IsFaulted))
                {
                    sb.Append("User load task for user " + loadTaskIds[t.Id] + " failed: ");
                    foreach (Exception x in t.Exception.Flatten().InnerExceptions)
                    {
                        sb.Append(x.Message + " at " + x.StackTrace);
                        sb.Append(Environment.NewLine);
                    }
                }
                if (sb.Length > 0)
                    log(sb.ToString(), 2);
            }
            log("Waiting for user load tasks complete, duration " + DateTime.Now.Subtract(start).TotalMilliseconds + " ms", 4);
            List<AlcatelXmlApi6.AlcFwManagement.AlcFrameworkUser> usersToLoadOts = new List<AlcatelXmlApi6.AlcFwManagement.AlcFrameworkUser>();
            foreach (Task<AlcFrameworkUserOperationResult<AlcatelXmlApi6.AlcFwManagement.AlcFrameworkUser>> loadTask in userLoadTasks.Where(t => t.IsCompleted))
            {
                AlcFrameworkUserOperationResult<AlcatelXmlApi6.AlcFwManagement.AlcFrameworkUser> userRes = loadTask.Result;
                if (!userRes.Success)
                {
                    string userId = loadTaskIds[loadTask.Id];
                    log("Unable to extract user " + userId + " from server, " + ServerURL, 2);
                    usersFailedToLoad.Add(userId);
                    retval = false;
                }
                else
                {
                    if (connector.HasOtsLine(userRes.ResultObject))
                    {
                        usersToLoadOts.Add(userRes.ResultObject);
                    }
                }
            }
            if (userLoadTasks.Where(t => !t.IsCompleted).Count() > 0)
                retval = false;

            List<Task> dataLoadTasks = new List<Task>();
            List<Task<AlcFrameworkUserOperationResult<AlcatelXmlApi6.AlcFwManagement.AlcOtsAccount>>> otsLoadTasks = 
                new List<Task<AlcFrameworkUserOperationResult<AlcatelXmlApi6.AlcFwManagement.AlcOtsAccount>>>();
            List<Task<AlcPhoneOperationResult<VoiceMailInfo>>> vmLoadTasks = new List<Task<AlcPhoneOperationResult<VoiceMailInfo>>>();
            Dictionary<int, string> otsLoadTaskIds = new Dictionary<int, string>();
            Dictionary<int, string> vmLoadTaskIds = new Dictionary<int, string>();

            foreach (AlcatelXmlApi6.AlcFwManagement.AlcFrameworkUser user in usersToLoadOts)
            {
                Task<AlcFrameworkUserOperationResult<AlcatelXmlApi6.AlcFwManagement.AlcOtsAccount>> otsTask = 
                    connector.GetOtsAccountAsync(user.companyContacts.officePhone, true);
                dataLoadTasks.Add(otsTask);
                otsLoadTaskIds.Add(otsTask.Id, user.loginName);
                otsLoadTasks.Add(otsTask);
                Task<AlcPhoneOperationResult<VoiceMailInfo>> vmTask = connector.GetVoiceMailInfoAsync(user.companyContacts.officePhone);
                dataLoadTasks.Add(vmTask);
                vmLoadTaskIds.Add(vmTask.Id, user.loginName);
                vmLoadTasks.Add(vmTask);
            }
            try
            {
                await Task.WhenAll(dataLoadTasks).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                int nbCompleted = dataLoadTasks.Where(t => t.IsCompleted).Count();
                int nbFaulted = dataLoadTasks.Where(t => t.IsFaulted).Count();
                log("Exception when loading ots data / vm data from server " + ServerURL + ": " + e.Message + ", nb completed " + nbCompleted + " nb faulted "
                    + nbFaulted, 2);
                StringBuilder sb = new StringBuilder();
                if (nbFaulted > 0)
                    sb.Append("Dumping errors of faulted tasks: ");
                foreach (Task t in dataLoadTasks.Where(t => t.IsFaulted))
                {

                    if (otsLoadTaskIds.ContainsKey(t.Id))
                    {
                        sb.Append("ots load task for user " + otsLoadTaskIds[t.Id] + " failed: ");
                        if (!usersFailedToLoad.Contains(otsLoadTaskIds[t.Id]))
                            usersFailedToLoad.Add(otsLoadTaskIds[t.Id]);
                    }
                    else
                    {
                        sb.Append("vminfo load task for user " + vmLoadTaskIds[t.Id] + " failed: ");
                        if (!usersFailedToLoad.Contains(vmLoadTaskIds[t.Id]))
                            usersFailedToLoad.Add(vmLoadTaskIds[t.Id]);
                    }
                    foreach (Exception x in t.Exception.Flatten().InnerExceptions)
                    {
                        sb.Append(x.Message + " at " + x.StackTrace);
                        sb.Append(Environment.NewLine);
                    }
                }
                if (sb.Length > 0)
                    log(sb.ToString(), 2);
            }
            TimeSpan duration = DateTime.Now.Subtract(start);
            foreach (Task<AlcFrameworkUserOperationResult<AlcatelXmlApi6.AlcFwManagement.AlcOtsAccount>> otsTask in 
                otsLoadTasks.Where(t => t.Status == TaskStatus.RanToCompletion))
            {
                if (!otsTask.Result.Success)
                {
                    log("Unable to load ots data of user " + otsLoadTaskIds[otsTask.Id] + ": " + otsTask.Result.ToString(), 2);
                    if (!usersFailedToLoad.Contains(otsLoadTaskIds[otsTask.Id]))
                        usersFailedToLoad.Add(otsLoadTaskIds[otsTask.Id]);
                }
            }
            foreach (Task<AlcPhoneOperationResult<VoiceMailInfo>> vmLoadTask in vmLoadTasks.Where(t => t.Status == TaskStatus.RanToCompletion))
            {
                if (!vmLoadTask.Result.Success)
                {
                    log("Unable to load voicemail data of user " + vmLoadTasks[vmLoadTask.Id] + ": " + vmLoadTask.Result.ToString(), 2);
                    if (!usersFailedToLoad.Contains(vmLoadTaskIds[vmLoadTask.Id]))
                        usersFailedToLoad.Add(vmLoadTaskIds[vmLoadTask.Id]);
                }
            }
            if (dataLoadTasks.Where(t => !t.IsCompleted).Count() > 0)
                retval = false;
            log("Duration for sequential async user loading: " + duration.TotalMilliseconds, 4);
            log("Result of framework user loading: " + retval + " nb failed " + usersFailedToLoad.Count, 4);
        }

        public async Task LoadUsersAsync(List<string> userIds, bool useDoubleAsync = false)
        {
            bool retval = true;
            ConcurrentBag<string> usersFailedToLoad = new ConcurrentBag<string>();
            List<Task<bool>> loadTasks = new List<Task<bool>>();
            Dictionary<int, string> loadTaskIds = new Dictionary<int, string>();
            DateTime start = DateTime.Now;
            foreach (string userId in userIds)
            {
                Task<bool> loadTask = null;
                if (useDoubleAsync)
                    loadTask = loadUserAsyncDouble(userId);
                else
                    loadTask = loadUserAsync(userId);
                loadTaskIds.Add(loadTask.Id, userId);
                loadTasks.Add(loadTask);
            }
            try
            {
                await Task.WhenAll(loadTasks).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                int nbCompleted = loadTasks.Where(t => t.IsCompleted).Count();
                int nbFaulted = loadTasks.Where(t => t.IsFaulted).Count();
                log("Exception when loading users from server " + ServerURL + ": " + e.Message + ", nb completed " + nbCompleted + " nb faulted "
                    + nbFaulted, 2);
                StringBuilder sb = new StringBuilder();
                if (nbFaulted > 0)
                    sb.Append("Dumping errors of faulted tasks: ");
                foreach (Task t in loadTasks.Where(t => t.IsFaulted))
                {
                    sb.Append("User load task for user " + loadTaskIds[t.Id] + " failed: ");
                    foreach (Exception x in t.Exception.Flatten().InnerExceptions)
                    {
                        sb.Append(x.Message + " at " + x.StackTrace);
                        sb.Append(Environment.NewLine);
                    }
                }
            }
            TimeSpan duration = DateTime.Now.Subtract(start);
            foreach (Task<bool> loadTask in loadTasks.Where(t => t.IsCompleted))
            {
                bool userRes = loadTask.Result;
                if (!userRes)
                {
                    string userId = loadTaskIds[loadTask.Id];
                    log("Unable to extract user " + userId + " from server, " + ServerURL, 2);
                    usersFailedToLoad.Add(userId);
                    retval = false;
                }
            }
            if (loadTasks.Where(t => !t.IsCompleted).Count() > 0)
                retval = false;
            log("Duration for async user loading: " + duration.TotalMilliseconds, 4);
            log("Result of framework user loading: " + retval + " nb failed " + usersFailedToLoad.Count, 4);
        }

        /// <summary>
        /// load user data async, then ots/vminfo async but in parallel
        /// </summary>
        /// <param name="userId"></param>
        /// <param name="usersFailedToLoad"></param>
        /// <returns></returns>
        private async Task<bool> loadUserAsyncDouble(string userId)
        {
            try
            {
                log("Starting to load user " + userId, 5);
                AlcFrameworkUserOperationResult<AlcatelXmlApi6.AlcFwManagement.AlcFrameworkUser> userRes = await connector.GetFrameworkUserAsync(userId, true)
                    .ConfigureAwait(false);
                log("Loading of user " + userId + " complete", 5);
                if (userRes.Success)
                {
                    string line = userRes.ResultObject.companyContacts.officePhone;
                    if (connector.HasOtsLine(userRes.ResultObject))
                    {
                        log("Extracting ots line " + line + " for user " + userId, 5);
                        Task<AlcFrameworkUserOperationResult<AlcatelXmlApi6.AlcFwManagement.AlcOtsAccount>> otsTask = connector.GetOtsAccountAsync(line, true);
                        Task<AlcPhoneOperationResult<VoiceMailInfo>> vmTask = connector.GetVoiceMailInfoAsync(line);
                        try
                        {
                            await Task.WhenAll(vmTask, otsTask).ConfigureAwait(false);
                        }
                        catch (Exception e)
                        {
                            log("Exception when awaiting vm/ots data loading task of line " + line + " of user " + userId + ": "
                                + e.Message + " at " + e.StackTrace, 2);
                            return false;
                        }
                        if (vmTask.IsCompleted)
                        {
                            if (vmTask.Result.Success)
                            {
                                log("Extraction of voicemail info of line " + line + " of user " + userId + " from server " + ServerURL + " was successful", 5);
                            }
                            else
                            {
                                log("Extracting voicemail info of line " + line + " of user " + userId + " from server " + ServerURL + " failed: "
                                    + vmTask.Result.ToString(), 3);
                                return false;
                            }
                        }
                        else
                        {
                            log("voicemail extraction task for user " + userId + " line " + line + " did not run to completion. Status: " + vmTask.Status, 2);
                            return false;
                        }
                        if (otsTask.IsCompleted)
                        {
                            if (otsTask.IsCompleted)
                            {
                                log("Extraction of ots line " + line + " of user " + userId + " from server " + ServerURL
                                + " was successful", 5);
                            }
                            else
                            {
                                log("Extracting ots line " + userRes.ResultObject.companyContacts.officePhone + " of user " + userId + " from server " + ServerURL
                                    + " failed: " + otsTask.Result.ToString(), 2);
                                return false;
                            }
                        }
                        else
                        {
                            log("ots extraction task for user " + userId + " line " + line + " did not run to completion. Status: " + otsTask.Status, 2);
                            return false;
                        }
                        return true;
                    }
                    else
                    {
                        return true;
                    }
                }
            }
            catch (Exception e)
            {
                processException(e);
            }
            return false;
        }

        /// <summary>
        /// load user data sequentially using async
        /// </summary>
        /// <param name="userId"></param>
        /// <param name="usersFailedToLoad"></param>
        /// <returns></returns>
        private async Task<bool> loadUserAsync(string userId)
        {
            try
            {
                log("Starting to load user " + userId, 5);
                AlcFrameworkUserOperationResult<AlcatelXmlApi6.AlcFwManagement.AlcFrameworkUser> userRes = await connector.GetFrameworkUserAsync(userId, true)
                    .ConfigureAwait(false);
                log("Loading of user " + userId + " complete", 5);
                if (userRes.Success)
                {
                    string line = userRes.ResultObject.companyContacts.officePhone;
                    if (connector.HasOtsLine(userRes.ResultObject))
                    {
                        log("Extracting ots line " + line + " for user " + userId, 5);
                        AlcFrameworkUserOperationResult<AlcatelXmlApi6.AlcFwManagement.AlcOtsAccount> otsResult = await connector.GetOtsAccountAsync(line, true)
                            .ConfigureAwait(false);
                        if (otsResult.Success)
                            log("Extracting ots line " + line + " for user " + userId + " completed", 5);
                        else
                            log("extracting ots line " + line + " for user " + userId + " failed: " + otsResult.ToString(), 2);
                        log("Extracting vminfo for line " + line + " for user " + userId, 5);
                        AlcPhoneOperationResult<VoiceMailInfo> vmInfoResult = await connector.GetVoiceMailInfoAsync(line).ConfigureAwait(false);
                        if (vmInfoResult.Success)
                            log("Extracting vminfo for line " + line + " for user " + userId + " completed", 5);
                        else
                            log("Extracting vminfo for line " + line + " for user " + userId + " failed: " + vmInfoResult.ToString(), 5);
                        if (otsResult.Success && vmInfoResult.Success)
                        {
                            return true;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                processException(e);
            }
            return false;
        }

        /// <summary>
        /// load all user data sequentially
        /// </summary>
        /// <param name="userId"></param>
        /// <param name="usersFailedToLoad"></param>
        /// <returns></returns>
        private bool loadUser(string userId, ConcurrentBag<string> usersFailedToLoad)
        {
            try
            {
                log("Starting to load user " + userId, 5);
                dumpThreadPoolConfig();
                AlcFrameworkUserOperationResult<AlcatelXmlApi6.AlcFwManagement.AlcFrameworkUser> userRes = connector.GetFrameworkUser(userId, true);
                dumpThreadPoolConfig();
                log("Loading of user " + userId + " complete", 5);
                if (userRes.Success)
                {
                    string line = userRes.ResultObject.companyContacts.officePhone;
                    if (connector.HasOtsLine(userRes.ResultObject))
                    {
                        log("Extracting ots line " + line + " for user " + userId, 5);
                        AlcFrameworkUserOperationResult<AlcatelXmlApi6.AlcFwManagement.AlcOtsAccount> otsResult = connector.GetOtsAccount(line, true);
                        if (otsResult.Success)
                            log("Extracting ots line " + line + " for user " + userId + " completed", 5);
                        else
                            log("extracting ots line " + line + " for user " + userId + " failed: " + otsResult.ToString(), 2);
                        log("Extracting vminfo for line " + line + " for user " + userId, 5);
                        AlcPhoneOperationResult<VoiceMailInfo> vmInfoResult = connector.GetVoiceMailInfo(line);
                        if (vmInfoResult.Success)
                            log("Extracting vminfo for line " + line + " for user " + userId + " completed", 5);
                        else
                            log("Extracting vminfo for line " + line + " for user " + userId + " failed: " + vmInfoResult.ToString(), 2);
                        if (!otsResult.Success || !vmInfoResult.Success)
                            usersFailedToLoad.Add(userId);
                        else
                        {
                            return true;
                        }
                    }
                    else
                    {
                        log("User " + userId + " has no ots line, skip loading of ots line / vminfo", 4);
                        return true;
                    }
                }
                else
                {
                    log("Unable to load user " + userRes.ToString(), 2);
                    usersFailedToLoad.Add(userId);
                }
            }
            catch (Exception e)
            {
                processException(e);
            }
            return false;
        }

        private void processException(Exception e, [CallerMemberNameAttribute] string memberName = null)
        {
            if (e is AggregateException)
            {
                AggregateException aex = e as AggregateException;
                foreach (Exception x in aex.Flatten().InnerExceptions)
                    log("Aggregate exception in " + memberName + " : " + x.Message + " at " + x.StackTrace, 2);
            }
            else
                log("Exception in " + memberName + " : " + e.Message + " at " + e.StackTrace, 2);
        }

        private void log(string message, int severity)
        {
            if (severity <= LogLevel)
                Console.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " " + message);
        }

    }
}
