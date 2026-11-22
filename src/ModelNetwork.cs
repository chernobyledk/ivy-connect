using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;

internal static class ModelNetwork
{
    internal const string System = "system", Direct = "direct", Proxy = "proxy";

    internal sealed class ExplicitHttpProxy : IWebProxy
    {
        private readonly Uri address;
        internal ExplicitHttpProxy(Uri address) { this.address = address; }
        public ICredentials Credentials { get; set; }
        public Uri GetProxy(Uri destination) { return address; }
        public bool IsBypassed(Uri destination) { return false; }
    }

    internal static Uri Validate(PrivateModelSettings settings)
    {
        if (settings.Network == System || settings.Network == Direct) return null;
        Uri url;
        if (settings.Network != Proxy || !TryProxy(settings.ProxyAddress, out url))
            throw new ModelReplyException("proxy-setting", "请输入已有 HTTP 代理的地址及端口，例如 http://127.0.0.1:7890。");
        if ((settings.ProxyUser ?? "").Length > 128 || (settings.ProxyPassword ?? "").Length > 512 ||
            (!String.IsNullOrEmpty(settings.ProxyPassword) && String.IsNullOrWhiteSpace(settings.ProxyUser)))
            throw new ModelReplyException("proxy-setting", "代理认证须先填写用户名，请检查凭据长度。");
        return url;
    }

    internal static bool TryProxy(string address, out Uri url)
    {
        return Uri.TryCreate(address, UriKind.Absolute, out url) && url.Scheme == "http" &&
            url.AbsolutePath == "/" && url.UserInfo.Length == 0 && url.Query.Length == 0 &&
            url.Fragment.Length == 0 && url.Port > 0;
    }

    internal static bool SameProxy(string first, string second)
    {
        Uri a, b;
        return TryProxy(first, out a) && TryProxy(second, out b) && a.IdnHost == b.IdnHost && a.Port == b.Port;
    }

    internal static string Label(string mode)
    {
        if (mode == Direct) return "直接连接";
        if (mode == Proxy) return "自定义 HTTP 代理";
        return "跟随系统";
    }

    internal static HttpClientHandler Handler(PrivateModelSettings settings)
    {
        var proxy = Validate(settings);
        var handler = new HttpClientHandler {
            AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false, UseProxy = settings.Network != Direct
        };
        if (proxy != null)
        {
            var explicitProxy = new ExplicitHttpProxy(proxy);
            if (!String.IsNullOrWhiteSpace(settings.ProxyUser))
                explicitProxy.Credentials = new NetworkCredential(settings.ProxyUser, settings.ProxyPassword ?? "");
            handler.Proxy = explicitProxy;
        }
        return handler;
    }

    internal static ModelReplyException TransportError(HttpRequestException error, string network)
    {
#if NET10_0_OR_GREATER
        if (error.StatusCode == HttpStatusCode.ProxyAuthenticationRequired)
            return new ModelReplyException("proxy-auth", "代理要求认证或凭据未通过（HTTP 407）");
        if (error.HttpRequestError == HttpRequestError.NameResolutionError)
            return new ModelReplyException(network == Proxy ? "proxy-dns" : "dns", "无法解析当前网络路径的域名");
        if (error.HttpRequestError == HttpRequestError.SecureConnectionError)
            return new ModelReplyException("tls", "HTTPS 证书或 TLS 握手失败");
        if (error.HttpRequestError == HttpRequestError.ProxyTunnelError)
            return new ModelReplyException("proxy-tunnel", "HTTP 代理未能建立 HTTPS 隧道，请检查代理状态和凭据");
#endif
        for (Exception current = error; current != null; current = current.InnerException)
        {
            if (current is AuthenticationException)
                return new ModelReplyException("tls", "HTTPS 证书或 TLS 握手失败");
            var web = current as WebException;
            if (web != null)
            {
                var response = web.Response as HttpWebResponse;
                if (response != null && (int)response.StatusCode == 407)
                    return new ModelReplyException("proxy-auth", "代理要求认证或凭据未通过（HTTP 407）");
                switch (web.Status)
                {
                    case WebExceptionStatus.ProxyNameResolutionFailure: return new ModelReplyException("proxy-dns", "无法解析代理域名");
                    case WebExceptionStatus.NameResolutionFailure: return new ModelReplyException("dns", "无法解析模型服务域名");
                    case WebExceptionStatus.TrustFailure:
                    case WebExceptionStatus.SecureChannelFailure: return new ModelReplyException("tls", "HTTPS 证书或 TLS 握手失败");
                    case WebExceptionStatus.Timeout: return new ModelReplyException("timeout", "模型网络请求超时，未自动重试");
                    case WebExceptionStatus.ConnectFailure:
                        if (network == Proxy) return new ModelReplyException("proxy-connect", "无法连接已配置的 HTTP 代理");
                        break;
                }
            }
            var socket = current as SocketException;
            if (socket != null && (socket.SocketErrorCode == SocketError.HostNotFound || socket.SocketErrorCode == SocketError.NoData))
                return new ModelReplyException(network == Proxy ? "proxy-dns" : "dns",
                    network == Proxy ? "代理路径出现域名解析失败" : "模型网络路径出现域名解析失败");
        }
        return new ModelReplyException("connection", "网络连接中断、响应超限或代理路径失败，未能进一步定位");
    }
}
