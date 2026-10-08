namespace FactoryQuoteApi.Services;

public interface IQuoteSequenceService
{
    Task EnsureInitializedAsync();
    Task<string> GetNextQuoteNumberAsync(DateTime quoteDate);
    Task<string> PeekNextQuoteNumberAsync(DateTime quoteDate);
}
