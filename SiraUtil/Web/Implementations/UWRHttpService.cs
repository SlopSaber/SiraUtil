using IPA.Utilities;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace SiraUtil.Web.Implementations
{
    internal class UWRHttpService : IHttpService
    {
        private sealed class RequestSettings
        {
            public readonly string URL;
            public readonly int Timeout;
            public readonly KeyValuePair<string, string>[] Headers;
            public readonly KeyValuePair<string, string>[]? ExtraHeaders;

            public RequestSettings(string url, int timeout, KeyValuePair<string, string>[] headers, KeyValuePair<string, string>[]? extraHeaders)
            {
                URL = url;
                Timeout = timeout;
                Headers = headers;
                ExtraHeaders = extraHeaders;
            }
        }

        private readonly object _admissionLock = new();
        private Task _kickoffTail = Task.CompletedTask;

        public IDictionary<string, string> Headers { get; private set; } = new Dictionary<string, string>();

        public string? Token
        {
            set
            {
                if (value is null)
                {
                    if (Headers.ContainsKey("Authorization"))
                    {
                        Headers.Remove("Authorization");
                    }
                }

                if (value is not null)
                {
                    Headers["Authorization"] = $"Bearer {value}";
                }
            }
        }

        public string? BaseURL { get; set; }

        public string? UserAgent
        {
            get => Headers.TryGetValue("User-Agent", out string? value) ? value : null;
            set
            {
                if (value is null)
                {
                    if (Headers.ContainsKey("User-Agent"))
                    {
                        Headers.Remove("User-Agent");
                    }
                }

                if (value is not null)
                {
                    Headers["User-Agent"] = value;
                }
            }
        }

        public int Timeout { get; set; } = 60;

        public Task<IHttpResponse> GetAsync(string url, IProgress<float>? progress = null, CancellationToken? cancellationToken = null)
        {
            return SendAsync(HTTPMethod.GET, url, null, null, progress, cancellationToken);
        }

        public Task<IHttpResponse> GetAsync(string url, int timeout, IProgress<float>? progress = null, CancellationToken? cancellationToken = null)
        {
            return SendAsync(HTTPMethod.GET, url, timeout, null, null, progress, cancellationToken);
        }

        public Task<IHttpResponse> PostAsync(string url, object? body = null, CancellationToken? cancellationToken = null)
        {
            return SendAsync(HTTPMethod.POST, url, JsonConvert.SerializeObject(body), null, null, cancellationToken);
        }

        public Task<IHttpResponse> PostAsync(string url, int timeout, object? body = null, CancellationToken? cancellationToken = null)
        {
            return SendAsync(HTTPMethod.POST, url, timeout, JsonConvert.SerializeObject(body), null, null, cancellationToken);
        }

        public Task<IHttpResponse> PutAsync(string url, object? body = null, CancellationToken? cancellationToken = null)
        {
            return SendAsync(HTTPMethod.PUT, url, JsonConvert.SerializeObject(body), null, null, cancellationToken);
        }

        public Task<IHttpResponse> PutAsync(string url, int timeout, object? body = null, CancellationToken? cancellationToken = null)
        {
            return SendAsync(HTTPMethod.PUT, url, timeout, JsonConvert.SerializeObject(body), null, null, cancellationToken);
        }

        public Task<IHttpResponse> PatchAsync(string url, object? body = null, CancellationToken? cancellationToken = null)
        {
            return SendAsync(HTTPMethod.PATCH, url, JsonConvert.SerializeObject(body), null, null, cancellationToken);
        }

        public Task<IHttpResponse> PatchAsync(string url, int timeout, object? body = null, CancellationToken? cancellationToken = null)
        {
            return SendAsync(HTTPMethod.PATCH, url, timeout, JsonConvert.SerializeObject(body), null, null, cancellationToken);
        }

        public Task<IHttpResponse> DeleteAsync(string url, CancellationToken? cancellationToken = null)
        {
            return SendAsync(HTTPMethod.DELETE, url, null, null, null, cancellationToken);
        }

        public Task<IHttpResponse> DeleteAsync(string url, int timeout, CancellationToken? cancellationToken = null)
        {
            return SendAsync(HTTPMethod.DELETE, url, timeout, null, null, null, cancellationToken);
        }

        public Task<IHttpResponse> SendAsync(HTTPMethod method, string url, string? body = null, IDictionary<string, string>? withHeaders = null, IProgress<float>? downloadProgress = null, CancellationToken? cancellationToken = null)
        {
            if (body is not null)
            {
                withHeaders ??= new Dictionary<string, string>();
                withHeaders.Add("Content-Type", "application/json");
            }
            // Custom header enumerators can submit nested requests before native kickoff.
            if (withHeaders is not null && withHeaders.GetType() != typeof(Dictionary<string, string>))
            {
                return SendRawCoreAsync(method, url, body is null ? null : Encoding.UTF8.GetBytes(body), withHeaders, downloadProgress, cancellationToken);
            }
            return body is null
                ? SendRawAsync(method, url, null, withHeaders, downloadProgress, cancellationToken)
                : SendEncodedAsync(method, url, body, withHeaders, downloadProgress, cancellationToken, null);
        }

        public Task<IHttpResponse> SendAsync(HTTPMethod method, string url, int timeout, string? body = null, IDictionary<string, string>? withHeaders = null, IProgress<float>? downloadProgress = null, CancellationToken? cancellationToken = null)
        {
            if (body is not null)
            {
                withHeaders ??= new Dictionary<string, string>();
                withHeaders.Add("Content-Type", "application/json");
            }
            if (withHeaders is not null && withHeaders.GetType() != typeof(Dictionary<string, string>))
            {
                return SendRawCoreAsync(method, url, body is null ? null : Encoding.UTF8.GetBytes(body), withHeaders, downloadProgress, cancellationToken, timeout);
            }
            return body is null
                ? SendRawAsync(method, url, null, withHeaders, downloadProgress, cancellationToken, timeout)
                : SendEncodedAsync(method, url, body, withHeaders, downloadProgress, cancellationToken, timeout);
        }

        public Task<IHttpResponse> SendRawAsync(HTTPMethod method, string url, byte[]? body = null, IDictionary<string, string>? withHeaders = null, IProgress<float>? downloadProgress = null, CancellationToken? cancellationToken = null, int? timeout = null)
        {
            if (withHeaders is not null && withHeaders.GetType() != typeof(Dictionary<string, string>))
            {
                return SendRawCoreAsync(method, url, body, withHeaders, downloadProgress, cancellationToken, timeout);
            }
            Task? predecessor = null;
            TaskCompletionSource<bool>? admission = null;
            lock (_admissionLock)
            {
                if (!_kickoffTail.IsCompleted)
                {
                    admission = ReserveAdmission(out predecessor);
                }
            }
            return admission is null
                ? SendRawCoreAsync(method, url, body, withHeaders, downloadProgress, cancellationToken, timeout)
                : SendPreparedAsync(method, url, null, body, withHeaders, downloadProgress, cancellationToken, timeout, predecessor!, admission);
        }

        private Task<IHttpResponse> SendEncodedAsync(HTTPMethod method, string url, string body, IDictionary<string, string>? withHeaders, IProgress<float>? downloadProgress, CancellationToken? cancellationToken, int? timeout)
        {
            Task predecessor;
            TaskCompletionSource<bool> admission;
            lock (_admissionLock)
            {
                admission = ReserveAdmission(out predecessor);
            }
            return SendPreparedAsync(method, url, body, null, withHeaders, downloadProgress, cancellationToken, timeout, predecessor, admission);
        }

        private TaskCompletionSource<bool> ReserveAdmission(out Task predecessor)
        {
            predecessor = _kickoffTail;
            var admission = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _kickoffTail = admission.Task;
            return admission;
        }

        private RequestSettings CaptureSettings(string url, int? timeout, IDictionary<string, string>? withHeaders)
        {
            string newURL = url;
            if (BaseURL != null)
            {
                newURL = Path.Combine(BaseURL, url);
            }
            int requestTimeout = timeout ?? Timeout;
            var headers = new List<KeyValuePair<string, string>>(Headers).ToArray();
            KeyValuePair<string, string>[]? extraHeaders = withHeaders is null
                ? null : new List<KeyValuePair<string, string>>(withHeaders).ToArray();
            return new RequestSettings(newURL, requestTimeout, headers, extraHeaders);
        }

        private async Task<IHttpResponse> SendPreparedAsync(HTTPMethod method, string url, string? textBody, byte[]? rawBody, IDictionary<string, string>? withHeaders, IProgress<float>? downloadProgress, CancellationToken? cancellationToken, int? timeout, Task predecessor, TaskCompletionSource<bool> admission)
        {
            try
            {
                await UnityGame.SwitchToMainThreadAsync();
                RequestSettings settings = CaptureSettings(url, timeout, withHeaders);
                byte[]? body = rawBody is null ? null : (byte[])rawBody.Clone();
                await predecessor.ConfigureAwait(false);
                if (textBody is not null)
                {
                    Task<byte[]> encoding = Task.Factory.StartNew(EncodeBody, textBody, CancellationToken.None,
                        TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
                    body = await encoding.ConfigureAwait(false);
                }
                await UnityGame.SwitchToMainThreadAsync();
                Task<IHttpResponse> response = SendRawCoreAsync(method, url, body, null, downloadProgress, cancellationToken, timeout, settings, admission);
                // A synchronous setup failure still has to release the next admission.
                admission.TrySetResult(true);
                return await response.ConfigureAwait(false);
            }
            finally
            {
                // Failure must not let a successor bypass an earlier pending request.
                await predecessor.ConfigureAwait(false);
                admission.TrySetResult(true);
            }
        }

        private static byte[] EncodeBody(object state)
        {
            return Encoding.UTF8.GetBytes((string)state);
        }

        private async Task<IHttpResponse> SendRawCoreAsync(HTTPMethod method, string url, byte[]? body = null, IDictionary<string, string>? withHeaders = null, IProgress<float>? downloadProgress = null, CancellationToken? cancellationToken = null, int? timeout = null, RequestSettings? settings = null, TaskCompletionSource<bool>? admission = null)
        {
            // UnityWebRequest must be created & executed on the main thread
            await UnityGame.SwitchToMainThreadAsync();

            string newURL = settings?.URL ?? url;
            if (settings is null && BaseURL != null)
            {
                newURL = Path.Combine(BaseURL, url);
            }

            DownloadHandler? dHandler = new DownloadHandlerBuffer();

            HTTPMethod originalMethod = method;
            if (method == HTTPMethod.POST && body != null)
            {
                method = HTTPMethod.PUT;
            }

            using UnityWebRequest request = new(newURL, method.ToString(), dHandler, body == null ? null : new UploadHandlerRaw(body));
            request.timeout = settings?.Timeout ?? timeout ?? Timeout;

            IEnumerable<KeyValuePair<string, string>> headers = settings?.Headers ?? (IEnumerable<KeyValuePair<string, string>>)Headers;
            foreach (KeyValuePair<string, string> header in headers)
            {
                request.SetRequestHeader(header.Key, header.Value);
            }

            IEnumerable<KeyValuePair<string, string>>? extraHeaders = settings is null ? withHeaders : settings.ExtraHeaders;
            if (extraHeaders != null)
            {
                foreach (KeyValuePair<string, string> header in extraHeaders)
                {
                    request.SetRequestHeader(header.Key, header.Value);
                }
            }

            // some unity bull
            if (body != null && originalMethod == HTTPMethod.POST && method == HTTPMethod.PUT)
            {
                request.method = originalMethod.ToString();
            }

            float lastProgress = -1f;
            AsyncOperation asyncOp = request.SendWebRequest();
            admission?.TrySetResult(true);
            while (!asyncOp.isDone)
            {
                if (cancellationToken is { IsCancellationRequested: true })
                {
                    request.Abort();
                    break;
                }
                if (downloadProgress is not null && dHandler is not null)
                {
                    float currentProgress = asyncOp.progress;
                    if (Math.Abs(lastProgress - currentProgress) > 0.001f)
                    {
                        downloadProgress.Report(currentProgress);
                        lastProgress = currentProgress;
                    }
                }
                await Task.Delay(10);
            }
            downloadProgress?.Report(1f);
            bool successful = request is { isDone: true, result: UnityWebRequest.Result.Success };
            return new UnityWebRequestHttpResponse(request, successful);
        }
    }
}
