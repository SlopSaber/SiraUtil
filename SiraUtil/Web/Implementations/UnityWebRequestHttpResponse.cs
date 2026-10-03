using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.Networking;

namespace SiraUtil.Web.Implementations
{
    internal class UnityWebRequestHttpResponse : IHttpResponse
    {
        public int Code { get; }
        public byte[] Bytes { get; }
        public bool Successful { get; }
        public Dictionary<string, string> Headers { get; }

        public UnityWebRequestHttpResponse(UnityWebRequest unityWebRequest, bool successful)
        {
            Successful = successful;
            Code = (int)unityWebRequest.responseCode;
            Bytes = unityWebRequest.downloadHandler.data;
            Headers = unityWebRequest.GetResponseHeaders();
        }

        public async Task<string?> Error()
        {
            if (Successful)
            {
                return null;
            }

            string body = await Task.FromResult(Encoding.UTF8.GetString(Bytes));
            if (body is null)
            {
                return Code.ToString();
            }

            try
            {
                return JsonConvert.DeserializeObject<ErrorBody>(body)!.Error;
            }
            catch
            {
                return Code.ToString();
            }
        }

        public Task<byte[]> ReadAsByteArrayAsync()
        {
            return Task.FromResult(Bytes);
        }

        public Task<Stream> ReadAsStreamAsync()
        {
            return Task.FromResult<Stream>(new MemoryStream(Bytes));
        }

        public Task<string> ReadAsStringAsync()
        {
            if (Bytes is null)
            {
                throw new ArgumentNullException("bytes");
            }

            if (Bytes.Length == 0)
            {
                return Task.FromResult(string.Empty);
            }

            byte[] ownedBytes = (byte[])Bytes.Clone();
            return Task.Factory.StartNew(
                static state => Encoding.UTF8.GetString((byte[])state!),
                ownedBytes,
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default);
        }

        private class ErrorBody
        {
            public string Error { get; set; } = null!;
        }
    }
}
