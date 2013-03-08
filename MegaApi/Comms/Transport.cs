﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net;
using Newtonsoft.Json.Linq;
using System.Diagnostics;
using Newtonsoft.Json;
using MegaApi.Comms.Requests;
using System.Timers;

namespace MegaApi.Comms
{
    public class Transport
    {
        static string ClientServerUrl = "https://g.api.mega.co.nz/cs";
        public MegaUser Auth { get; set; }
        PollingTransport polling;
        List<string> tracking = new List<string>();
        object pollingLock = new object();
        public event EventHandler<ServerRequestArgs> ServerRequest;
        public IWebProxy Proxy { get; set; }

        Queue<MegaRequest> requests = new Queue<MegaRequest>();

        public void EnqueueRequest(MegaRequest request)
        {
            lock (requests)
            {
                requests.Enqueue(request);
            }
            if (requests.Count == 1)
            {
                Utility.Util.StartThread(() => ProcessRequest());
            }
        }
        private void ProcessAgain(bool incrRetries = true)
        {
            if (incrRetries && requests.Count > 0) { requests.Peek().retries++; }
            ProcessRequest();
        }
        private void ProcessNext()
        {
            if (requests.Count < 1) { return; }
            requests.Dequeue();
            ProcessRequest();
        }
        private void ProcessError(MegaRequest req, int errno)
        {
            switch (errno)
            {
                case MegaApiError.EAGAIN:
                    if (req.retries >= 30)
                    {
                        ProcessNext();
                        req.HandleError(MegaApiError.EAPI);
                        break;
                    }
                    Debug.WriteLine("Received -3, retrying");
                    ProcessAgain();
                    break;

                case MegaApiError.EARGS:
                    ProcessNext();
                    req.HandleError(MegaApiError.EAPI);
                    break;

                case MegaApiError.ESID:
                    if (req.retries >= 3)
                    {
                        ProcessNext();
                        req.HandleError(MegaApiError.EBROKEN);
                    }
                    else
                    {
                        if (Auth == null) { req.HandleError(MegaApiError.EBROKEN); }
                        else
                        {
                            var sidReq = new MRequestGetSid<MResponseGetSid>(Auth);
                            sidReq.Success += (s, a) =>
                            {
                                req.Sid = a.SessionId;
                                ProcessAgain();
                            };
                            sidReq.Error += (s, a) => req.HandleError(MegaApiError.EBROKEN);
                            ProcessRequest(sidReq);
                        }
                    }
                    break;

                default:
                    ProcessNext();
                    req.HandleError(errno);
                    break;
            }
        }
        private void ProcessRequest(MegaRequest urgentRequest = null)
        {
            if (urgentRequest == null && requests.Count < 1) { return; }
            var req = urgentRequest == null ? requests.Peek() : urgentRequest;
            var wc = new WebClient();
            wc.Proxy = Proxy;
            wc.UploadStringCompleted += (s, e) =>
            {
                if (e.Error == null)
                {
                    try
                    {
                        var response = String.Format("{{root:{0}}}", e.Result);
                        var r = JObject.Parse(response)["root"];

                        #region error handling
                        if (r.Type == JTokenType.Integer && r.ToObject<int>() < 0)
                        {
                            ProcessError(req, r.ToObject<int>());
                            return;
                        }
                        if (r.Type == JTokenType.Array)
                        {
                            if (r[0].Type == JTokenType.Integer && r[0].ToObject<int>() < 0)
                            {
                                ProcessError(req, r[0].ToObject<int>());
                                return;
                            }
                        }
                        else
                        {
                            req.HandleError(MegaApiError.EUNEXPECTED);
                            return;
                        }
                        #endregion

                        ProcessNext();
                        req.HandleSuccess(r[0]);
                    }
                    catch (JsonException)
                    {
                        ProcessNext();
                        req.HandleError(MegaApiError.EUNEXPECTED);
                    }
                }
                else { ProcessAgain(); }
            };
            try
            {
                if (req.IsTrackig) { tracking.Add(((ITrackingRequest)req).TrackingId); }
                wc.UploadStringAsync(BuildCsUri(req), GetData(req));
            }
            catch (WebException) { ProcessAgain(false); }
        }

        private Uri BuildCsUri(MegaRequest req)
        {
            var url = ClientServerUrl;
            url += "?id=" + req.Id.ToString();
            url += req.Sid != null ? "&sid=" + req.Sid : "";
            url += req.NodeSid != null ? "&n=" + req.NodeSid : "";
            return new Uri(url);
        }

        private string GetData(MegaRequest req)
        {
            var t = JsonConvert.SerializeObject(req,
                new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore, Converters = req.Converters });
            return string.Format("[{0}]", t);
        }

        public static string Encode(byte[] data)
        {
            return Convert.ToBase64String(data)
                .Replace('+', '-')
                .Replace('/', '_')
                .TrimEnd(new char[] { '=' });
        }
        public static byte[] Decode(string data)
        {
            var add = string.Empty;
            if (data.Length % 4 != 0) { add = new String('=', 4 - data.Length % 4); }
            data = data
                .Replace('-', '+')
                .Replace('_', '/')
                + add;
            return Convert.FromBase64String(data);
        }

        internal void StartPoll(MegaRequest cause, string handle)
        {
            lock (pollingLock)
            {
                if (polling != null) { polling.Cancel(); }
                polling = new PollingTransport(Auth);
                polling.Proxy = Proxy;
                polling.tracking = tracking;
                polling.ServerCommand += (s, e) =>
                {
                    if (ServerRequest != null)
                    {
                        ServerRequest(s, e);
                    }
                };
                polling.StartPoll(cause, handle);
            }
        }


    }

}
