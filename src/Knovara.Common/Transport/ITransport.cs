namespace Knovara.Common.Transport
{
    using System.Threading.Tasks;

    public interface ITransport
    {
        Task<TResponse> SendAsync<TRequest, TResponse>(TRequest request)
            where TRequest : class
            where TResponse : class;
    }
}
