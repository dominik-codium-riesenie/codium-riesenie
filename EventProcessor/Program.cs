using Microsoft.Data.SqlClient;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace EventProcessor
{
    public static class AppConfig
    {
        public const string SourceJsonPath = "zdrojovy_dokument.json";

        public const int MaxDatabaseConnections = 10;

        public const bool CreateDatabaseTablesAtStartup = true;

        // SQL Server má limit 2100 parametrov na jednu query.
        public const int MaxParametersPerSqlCommand = 1800;

        public const string ConnectionStringEnvVariable = "DB_CONNECTION_STRING";
    }
    
    public class Program
    {
        public static async Task Main(string[] args)
        {
            Console.WriteLine("\n---------------------------------------------------");
            Console.WriteLine("--  Aplikácia na spracovanie športových eventov  --");
            Console.WriteLine("---------------------------------------------------\n");

            string? connectionString = Environment.GetEnvironmentVariable(AppConfig.ConnectionStringEnvVariable);

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                var configuration = new ConfigurationBuilder()
                    .SetBasePath(Directory.GetCurrentDirectory())
                    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                    .Build();

                connectionString = configuration.GetConnectionString("DefaultConnection");
            }

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine(
                    $"Chyba: Connection string nebol nájdený v appsettings.json ani v premennej prostredia '{AppConfig.ConnectionStringEnvVariable}'.");
                Console.WriteLine("Nastavte premennú a skúste to znova.");
                Console.ResetColor();
                return;
            }

            Console.WriteLine("OK: Connection string úspešne načítaný.\n");


            if (!File.Exists(AppConfig.SourceJsonPath))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Chyba: Zdrojový súbor '{AppConfig.SourceJsonPath}' nebol nájdený.");
                Console.WriteLine("Uistite sa, že súbor existuje v rovnakom adresári ako aplikácia.");
                Console.ResetColor();
                return;
            }

            Console.WriteLine($"OK: Zdrojový súbor '{AppConfig.SourceJsonPath}' nájdený.\n");


            if (AppConfig.CreateDatabaseTablesAtStartup)
            {
                await EnsureDatabaseTablesExistAsync(connectionString);
            }


            var stopwatch = Stopwatch.StartNew();

            Console.WriteLine("\nNačítavanie a spracovanie dát v pamäti...");
            var processedEvents = LoadAndProcessEventsInMemory(AppConfig.SourceJsonPath);
            Console.WriteLine($"OK: {processedEvents.Count} eventov bolo spracovaných v rámci pamäte.");

            Console.WriteLine("\nSpúšťa sa paralelné ukladanie do databázy...");
            await InsertEventsToDatabaseAsync(processedEvents, connectionString);

            stopwatch.Stop();
            Console.WriteLine("\nKoniec programu.");
            Console.WriteLine($"Celkový čas spracovania: {stopwatch.Elapsed.TotalSeconds:F2} sekúnd.");
        }


        private static async Task EnsureDatabaseTablesExistAsync(string connectionString)
        {
            Console.WriteLine("Kontrolujem existenciu databázových tabuliek...");
            try
            {
                using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync();

                string createEventsTableSql = @"
                IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='Events' and xtype='U')
                CREATE TABLE Events (
                    ID INT PRIMARY KEY IDENTITY(1,1),
                    ProviderEventID BIGINT NOT NULL UNIQUE,
                    EventName NVARCHAR(255) NOT NULL,
                    EventDate DATETIME2 NOT NULL,
                    LastUpdated DATETIME2 DEFAULT GETUTCDATE()
                );";

                string createOddsTableSql = @"
                IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='Odds' and xtype='U')
                CREATE TABLE Odds (
                    ID INT PRIMARY KEY IDENTITY(1,1),
                    ProviderOddsID BIGINT NOT NULL UNIQUE,
                    EventID INT NOT NULL,
                    OddsName NVARCHAR(100) NOT NULL,
                    OddsRate FLOAT NOT NULL,
                    Status VARCHAR(50) NOT NULL,
                    LastUpdated DATETIME2 DEFAULT GETUTCDATE(),
                    CONSTRAINT FK_Odds_EventID FOREIGN KEY (EventID) REFERENCES Events(ID) ON DELETE CASCADE
                );";

                await using (var command = connection.CreateCommand())
                {
                    command.CommandText = createEventsTableSql;
                    await command.ExecuteNonQueryAsync();

                    command.CommandText = createOddsTableSql;
                    await command.ExecuteNonQueryAsync();

                    Console.WriteLine("OK: Databázové tabuľky boli vytvorené, ak neexistovali.\n");
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Chyba pri vytváraní databázových tabuliek:\n{ex.Message}");
                Console.ResetColor();
                throw;
            }
        }


        private static List<Event> LoadAndProcessEventsInMemory(string filePath)
        {
            var jsonString = File.ReadAllText(filePath);
            var messages = JsonSerializer.Deserialize<List<Message>>(jsonString);

            var processedEvents = new Dictionary<long, Event>();
            
            if (messages is null)
            {
                throw new NullReferenceException("Deserialized messages are null.");
            }

            foreach (var message in messages)
            {
                var incomingEvent = message.Event;
                if (incomingEvent is null)
                {
                    continue;
                }

                var currentEvent = new Event();
                if (processedEvents.TryGetValue(incomingEvent.ProviderEventID, out var existingEvent))
                {
                    currentEvent = existingEvent;
                }
                else
                {
                    processedEvents.Add(incomingEvent.ProviderEventID, currentEvent);
                }

                currentEvent.ProviderEventID = incomingEvent.ProviderEventID;
                currentEvent.EventName = incomingEvent.EventName;
                currentEvent.EventDate = incomingEvent.EventDate;

                currentEvent.OddsList ??= new List<Odd>();

                if (incomingEvent.OddsList == null) continue;
                foreach (var incomingOdd in incomingEvent.OddsList)
                {
                    var currentOdd = currentEvent.OddsList.FirstOrDefault(odd => odd.ProviderOddsID == incomingOdd.ProviderOddsID);
                    if (currentOdd is not null)
                    {
                        currentOdd.OddsRate = incomingOdd.OddsRate;
                        currentOdd.Status = incomingOdd.Status;
                    }
                    else
                    {
                        currentEvent.OddsList.Add(incomingOdd);
                    }
                }
            }

            return processedEvents.Values.ToList();
        }


        private static async Task InsertEventsToDatabaseAsync(List<Event> events, string connectionString)
        {
            int totalEvents = events.Count;
            int processedCount = 0;
            var semaphore = new SemaphoreSlim(AppConfig.MaxDatabaseConnections);

            var tasks = events.Select(async currentEvent =>
            {
                // Simulácia volania externého API
                var randomDelay = new Random().Next(0, 10001);
                await Task.Delay(randomDelay);

                await semaphore.WaitAsync();
                try
                {
                    const int maxRetries = 3;
                    for (int attempt = 1; attempt <= maxRetries; attempt++)
                    {
                        try
                        {
                            await SaveEventToDatabaseAsync(currentEvent, connectionString);
                            break;
                        }
                        catch
                        {
                            await Task.Delay(TimeSpan.FromSeconds(Math.Pow(3, attempt)));

                            if (attempt == maxRetries)
                            {
                                throw;
                            }
                        }
                    }

                    int currentProcessed = Interlocked.Increment(ref processedCount);
                    Console.WriteLine($"\nSpracoval sa event {currentEvent.ProviderEventID}.\nZostáva spracovať: {totalEvents - currentProcessed} / {totalEvents}.");
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"\nChyba pri spracovaní eventu {currentEvent.ProviderEventID}\n{ex.Message}.");
                    Console.ResetColor();
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"\nÚspešne sa spracovalo {processedCount} / {totalEvents} eventov.");
            Console.ResetColor();
        }


        private static async Task SaveEventToDatabaseAsync(Event currentEvent, string connectionString)
        {
            using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            using var transaction = connection.BeginTransaction();

            try
            {
                var eventMergeSql = @"
                MERGE INTO Events AS target
                USING (SELECT @ProviderEventID AS ProviderEventID) AS source
                ON (target.ProviderEventID = source.ProviderEventID)
                WHEN MATCHED THEN
                    UPDATE SET EventName = @EventName, EventDate = @EventDate, LastUpdated = GETUTCDATE()
                WHEN NOT MATCHED THEN
                    INSERT (ProviderEventID, EventName, EventDate)
                    VALUES (@ProviderEventID, @EventName, @EventDate);
                
                SELECT ID FROM Events WHERE ProviderEventID = @ProviderEventID;";

                var eventCommand = new SqlCommand(eventMergeSql, connection, transaction);
                eventCommand.Parameters.AddWithValue("@ProviderEventID", currentEvent.ProviderEventID);
                eventCommand.Parameters.AddWithValue("@EventName", currentEvent.EventName);
                eventCommand.Parameters.AddWithValue("@EventDate", currentEvent.EventDate);

                var dbEventId = (int) (await eventCommand.ExecuteScalarAsync() ?? throw new InvalidOperationException("EventId is null."));

                const int paramsPerOdd = 5; // ProviderOddsID, EventID, OddsName, OddsRate, Status
                int maxOddsPerBatch = AppConfig.MaxParametersPerSqlCommand / paramsPerOdd;

                if (currentEvent.OddsList is null)
                {
                    throw new NullReferenceException("The event's odds list is null.");
                }
                    
                for (int i = 0; i < currentEvent.OddsList.Count; i += maxOddsPerBatch)
                {
                    var oddsBatch = currentEvent.OddsList.Skip(i).Take(maxOddsPerBatch).ToList();
                    if (!oddsBatch.Any()) continue;

                    var batchCommand = new SqlCommand { Connection = connection, Transaction = transaction };
                    var sqlBuilder = new StringBuilder();

                    for (int j = 0; j < oddsBatch.Count; j++)
                    {
                        var odd = oddsBatch[j];

                        // Vytvárame unikátne názvy parametrov pre každý kurz, aby nedošlo ku kolízii
                        sqlBuilder.AppendLine($@"
                        MERGE INTO Odds AS target
                        USING (SELECT @ProviderOddsID{j} AS ProviderOddsID) AS source
                        ON (target.ProviderOddsID = source.ProviderOddsID)
                        WHEN MATCHED THEN
                            UPDATE SET OddsRate = @OddsRate{j}, Status = @Status{j}, LastUpdated = GETUTCDATE()
                        WHEN NOT MATCHED THEN
                            INSERT (ProviderOddsID, EventID, OddsName, OddsRate, Status)
                            VALUES (@ProviderOddsID{j}, @EventID{j}, @OddsName{j}, @OddsRate{j}, @Status{j});");

                        batchCommand.Parameters.AddWithValue($"@ProviderOddsID{j}", odd.ProviderOddsID);
                        batchCommand.Parameters.AddWithValue($"@EventID{j}", dbEventId);
                        batchCommand.Parameters.AddWithValue($"@OddsName{j}", odd.OddsName);
                        batchCommand.Parameters.AddWithValue($"@OddsRate{j}", odd.OddsRate);
                        batchCommand.Parameters.AddWithValue($"@Status{j}", odd.Status);
                    }

                    batchCommand.CommandText = sqlBuilder.ToString();
                    await batchCommand.ExecuteNonQueryAsync();
                }

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }
    }
}