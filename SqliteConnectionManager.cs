using Microsoft.Data.Sqlite;
using Polly;
using Polly.Retry;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace sqlite_simulator;

public static class SqliteConnectionManager
{
    private static string _connectionString = string.Empty;
    private static readonly AsyncRetryPolicy _retryPolicy;
    static SqliteConnectionManager()
    {
        _retryPolicy = Policy
            .Handle<SqliteException>()
            .Or<TimeoutException>()
            .WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
                onRetry: (exception, timeSpan, retryCount, context) =>
                {
                    Logger.WriteErrorLog($"Database connection retry {retryCount} after {timeSpan.TotalSeconds}s due to: {exception.Message}");
                }
            );
    }
    public static async Task SetConnectionString(string connectionString)
    {
        _connectionString = connectionString;
        await Task.CompletedTask;
    }
    public static async Task<SqliteConnection> GetConnectionAsync()
    {
        if (string.IsNullOrEmpty(_connectionString))
        {
            throw new InvalidOperationException("Connection string has not been initialized. Call SetConnectionString first.");
        }
        return await _retryPolicy.ExecuteAsync(async () =>
        {
            // Cache=Shared allows multiple connections to the same in-memory/file DB to share state if needed,
            // though for file-based it's less critical but good practice with WAL.
            var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            if (connection.State != ConnectionState.Open)
            {
                throw new InvalidOperationException("Failed to open database connection.");
            }
            return connection;
        });
    }
    public static async Task<bool> TestConnectionAsync()
    {
        try
        {
            using var connection = await GetConnectionAsync();
            return connection.State == ConnectionState.Open;
        }
        catch (Exception ex)
        {
            Logger.WriteErrorLog($"Connection test failed: {ex.Message}");
            return false;
        }
    }
}
