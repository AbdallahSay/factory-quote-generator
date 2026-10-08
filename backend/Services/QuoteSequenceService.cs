using Microsoft.EntityFrameworkCore;
using FactoryQuoteApi.Data;

namespace FactoryQuoteApi.Services;

public class QuoteSequenceService : IQuoteSequenceService
{
    private readonly ApplicationDbContext _db;
    private readonly ILogger<QuoteSequenceService> _logger;

    public QuoteSequenceService(ApplicationDbContext db, ILogger<QuoteSequenceService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task EnsureInitializedAsync()
    {
        try
        {
            await _db.Database.ExecuteSqlRawAsync(@"
                IF NOT EXISTS (SELECT * FROM sys.sequences WHERE name = 'QuoteNumberSequence')
                BEGIN
                    DECLARE @start INT = (SELECT ISNULL(MAX(Id), 0) + 1 FROM Quotes);
                    DECLARE @sql NVARCHAR(MAX) = N'CREATE SEQUENCE QuoteNumberSequence AS INT START WITH ' + CAST(@start AS NVARCHAR(10)) + N' INCREMENT BY 1;';
                    EXEC sp_executesql @sql;
                END

                IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_Quotes_QuoteNumber_Unique' AND object_id = OBJECT_ID('Quotes'))
                BEGIN
                    CREATE UNIQUE NONCLUSTERED INDEX IX_Quotes_QuoteNumber_Unique ON Quotes(QuoteNumber);
                END
            ");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not verify QuoteNumberSequence or unique index in SQL Server (may already exist or not supported).");
        }
    }

    public async Task<string> GetNextQuoteNumberAsync(DateTime quoteDate)
    {
        int sequenceValue;
        try
        {
            using var cmd = _db.Database.GetDbConnection().CreateCommand();
            cmd.CommandText = "SELECT NEXT VALUE FOR QuoteNumberSequence;";
            if (cmd.Connection!.State != System.Data.ConnectionState.Open)
            {
                await _db.Database.OpenConnectionAsync();
            }
            var result = await cmd.ExecuteScalarAsync();
            sequenceValue = Convert.ToInt32(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get NEXT VALUE FOR QuoteNumberSequence. Utilizing fallback.");
            int count = await _db.Quotes.CountAsync();
            sequenceValue = count + 1;
        }

        // Required format: [UniqueSequence]-[dd]-[MM]-[yyyy] with 6 digits leading zeros
        return $"{sequenceValue:D6}-{quoteDate:dd-MM-yyyy}";
    }

    public async Task<string> PeekNextQuoteNumberAsync(DateTime quoteDate)
    {
        int sequenceValue;
        try
        {
            using var cmd = _db.Database.GetDbConnection().CreateCommand();
            cmd.CommandText = "SELECT CAST(current_value AS INT) FROM sys.sequences WHERE name = 'QuoteNumberSequence';";
            if (cmd.Connection!.State != System.Data.ConnectionState.Open)
            {
                await _db.Database.OpenConnectionAsync();
            }
            var result = await cmd.ExecuteScalarAsync();
            sequenceValue = result != null && result != DBNull.Value ? Convert.ToInt32(result) + 1 : 1;
        }
        catch
        {
            int count = await _db.Quotes.CountAsync();
            sequenceValue = count + 1;
        }

        return $"{sequenceValue:D6}-{quoteDate:dd-MM-yyyy}";
    }
}
