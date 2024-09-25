using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Web;
using Newtonsoft.Json.Linq;
using System.Linq;
using System.Data.SqlClient;
using System.IO;
using System.ServiceProcess;
using System.Threading;

public class MyWindowsService : ServiceBase
{
    private static string? selectedSiteId;
    private static string connectionString;
    private static string accessToken;
    private static string refreshToken;
    private Timer timer;

    public MyWindowsService()
    {
        this.ServiceName = "MyWindowsService";
        this.CanStop = true;
        this.CanPauseAndContinue = true;
        this.AutoLog = true;

        // Initialize event logging
        if (!EventLog.SourceExists("MyWindowsServiceSource"))
        {
            EventLog.CreateEventSource("MyWindowsServiceSource", "MyWindowsServiceLog");
        }
        this.EventLog.Source = "MyWindowsServiceSource";
        this.EventLog.Log = "MyWindowsServiceLog";
    }

protected override void OnStart(string[] args)
{
    try
    {
        Notas.Log("Service is starting..."); // Adicione log aqui
        connectionString = ReadConnectionStringFromFile("connectionString.txt");
        timer = new Timer(async state => await DoWork(), null, TimeSpan.Zero, TimeSpan.FromSeconds(10));
        Notas.Log("Service started successfully.");
    }
    catch (Exception ex)
    {
        Notas.Log("Error during service start: " + ex.Message);
    }
}

private async Task DoWork()
    {
        try
        {
             Notas.Log("Starting work execution."); // Adicione log aqui
            // Retrieve the access token
            (accessToken, refreshToken) = await RetrieveAccessTokenAndRefreshToken();

            if (string.IsNullOrEmpty(accessToken))
            {
                EventLog.WriteEntry("No access token found.");
                return;
            }

            // Select a site if not already selected
            if (string.IsNullOrEmpty(selectedSiteId))
            {
                await DisplaySiteSelectionMenu(accessToken);
            }

 Notas.Log("Work execution completed successfully.");
            // Check the database for pending requests
            await CheckDatabaseForPendingRequests(connectionString, accessToken, refreshToken);
        }
        catch (Exception ex)
        {
             Notas.Log("Error during work execution: " + ex.Message);
            EventLog.WriteEntry("Error during work execution: " + ex.Message, EventLogEntryType.Error);
        }
    }

    protected override void OnStop()
    {
        try
        {
            // Stop the timer when the service stops
            timer?.Change(Timeout.Infinite, 0);
            timer?.Dispose();

            EventLog.WriteEntry("Service stopped.");
        }
        catch (Exception ex)
        {
            // Log any error when stopping
            EventLog.WriteEntry("Error during service stop: " + ex.Message, EventLogEntryType.Error);
        }
    }

    // Method to read the connection string from a file
    private static string ReadConnectionStringFromFile(string filePath)
{
    try
    {
        // Cria o caminho absoluto do arquivo de conexão, baseado no diretório atual do serviço
        string absolutePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, filePath);
        
        // Lê o conteúdo do arquivo de string de conexão
        return File.ReadAllText(absolutePath).Trim();
    }
    catch (Exception ex)
    {
        throw new Exception("Error reading connection string: " + ex.Message);
    }
}

    // Retrieves the access token and refresh token
    private async Task<(string, string)> RetrieveAccessTokenAndRefreshToken()
    {
        try
        {
            using (var connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync();
                var query = "SELECT accessToken, refreshToken, tokenExpiration FROM requestConfig";
                using (var command = new SqlCommand(query, connection))
                {
                    using (var reader = await command.ExecuteReaderAsync())
                    {
                        if (await reader.ReadAsync())
                        {
                            var accessToken = reader["accessToken"].ToString();
                            var refreshToken = reader["refreshToken"].ToString();
                            var tokenExpiration = (DateTime)reader["tokenExpiration"];

                            // Check if the token is still valid
                            if (DateTime.UtcNow < tokenExpiration)
                            {
                                Notas.Log("Access token refreshed successfully.");
                                return (accessToken, refreshToken); // Token is valid
                            }
                            else
                            {
                                // Token expired, refresh it
                                return await RefreshAccessToken(refreshToken);
                            }
                        }
                        else
                        {
                            EventLog.WriteEntry("No token found in the database.");
                            return (null, null);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Notas.Log("Error during token refresh: " + ex.Message);
            EventLog.WriteEntry("Error retrieving token: " + ex.Message, EventLogEntryType.Error);
            return (null, null);
        }
    }

    // Refreshes the access token
    private async Task<(string, string)> RefreshAccessToken(string refreshToken)
    {
        try
        {
            var tokenUrl = "https://clp-accept-identityserver.saltoks.com/connect/token";
            var clientId = "956ebbbe-785a-4948-8592-ad2b826b0e6a";

            using (var httpClient = new HttpClient())
            {
                var parameters = new Dictionary<string, string>
                {
                    { "grant_type", "refresh_token" },
                    { "refresh_token", refreshToken },
                    { "client_id", clientId },
                    { "client_secret", "" } // Provide correct client_secret here
                };

                var requestContent = new FormUrlEncodedContent(parameters);
                var response = await httpClient.PostAsync(tokenUrl, requestContent);
                var content = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    var json = JObject.Parse(content);
                    var newAccessToken = json["access_token"]?.ToString();
                    var newRefreshToken = json["refresh_token"]?.ToString();
                    var expiresIn = json["expires_in"]?.ToObject<int>() ?? 3600;

                    await UpdateTokensInDatabase(newAccessToken, newRefreshToken, DateTime.UtcNow.AddSeconds(expiresIn));

Notas.Log("Refreshing access token."); // Adicione log aqui
                    return (newAccessToken, newRefreshToken);
                }
                else
                {
                    EventLog.WriteEntry("Error refreshing token: " + content, EventLogEntryType.Error);
                    return (null, null);
                }
            }
        }
        catch (Exception ex)
        {
            Notas.Log("Error during token refresh: " + ex.Message);
            EventLog.WriteEntry("Error during token refresh: " + ex.Message, EventLogEntryType.Error);
            return (null, null);
        }
    }

    private async Task UpdateTokensInDatabase(string newAccessToken, string newRefreshToken, DateTime expirationDate)
    {
        using (var connection = new SqlConnection(connectionString))
        {
            await connection.OpenAsync();
            var query = @"UPDATE requestConfig SET 
                            accessToken = @AccessToken, 
                            refreshToken = @RefreshToken, 
                            tokenExpiration = @TokenExpiration 
                          WHERE requestConfigID = 2";

            using (var command = new SqlCommand(query, connection))
            {
                command.Parameters.AddWithValue("@AccessToken", newAccessToken);
                command.Parameters.AddWithValue("@RefreshToken", newRefreshToken);
                command.Parameters.AddWithValue("@TokenExpiration", expirationDate);

                await command.ExecuteNonQueryAsync();
            }
        }
    }

    private async Task DisplaySiteSelectionMenu(string accessToken)
    {
        var apiUrl = "https://clp-accept-user.my-clay.com/v1.1/sites/";

        using (var httpClient = new HttpClient())
        {
            httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

            var response = await httpClient.GetAsync(apiUrl);
            var content = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                var json = JObject.Parse(content);
                var sites = json["items"]?.ToObject<JArray>();

                if (sites == null || !sites.Any())
                {
                    Notas.Log("Nenhum site encontrado");
                    EventLog.WriteEntry("Nenhum site encontrado.");
                    return;
                }

                var site = sites[0];
                selectedSiteId = site["id"]?.ToString();
                Notas.Log($"ID do Site Selecionado: {selectedSiteId}");
                EventLog.WriteEntry($"ID do Site Selecionado: {selectedSiteId}");
            }
            else
            {
                Notas.Log("Erro ao buscar sites: " + content);
                EventLog.WriteEntry("Erro ao buscar sites: " + content);
            }
        }
    }

    private async Task CheckDatabaseForPendingRequests(string connectionString, string accessToken, string refreshToken)
    {
        // Check if token is still valid before making the request
        if (string.IsNullOrEmpty(accessToken))
        {
            (accessToken, refreshToken) = await RefreshAccessToken(refreshToken);
            if (string.IsNullOrEmpty(accessToken))
            {
                Notas.Log("Erro ao renovar o token.");
                EventLog.WriteEntry("Erro ao renovar o token.");
                return;
            }
        }

        using (var connection = new SqlConnection(connectionString))
        {
            await connection.OpenAsync();

            var query = "SELECT TOP 1 recordID, protelRoomID, protelValidUntil FROM requestRecordsCode WHERE control = 'N'";
            using (var command = new SqlCommand(query, connection))
            {
                using (var reader = await command.ExecuteReaderAsync())
                {
                    if (await reader.ReadAsync())
                    {
                        var recordId = reader["recordID"].ToString();
                        var protelRoomID = reader["protelRoomID"].ToString();
                        var protelValidUntil = reader["protelValidUntil"] != DBNull.Value ? (DateTime?)reader["protelValidUntil"] : null;

                Notas.Log($"Requisição pendente encontrada. recordID: {recordId}, protelRoomID: {protelRoomID}");

                        EventLog.WriteEntry($"Requisição pendente encontrada. recordID: {recordId}, protelRoomID: {protelRoomID}");

                        reader.Close();

                        var selectedUserId = await GetUserByLastName(accessToken, protelRoomID);

                        if (!string.IsNullOrEmpty(selectedUserId))
                        {
                            var expiryDate = protelValidUntil ?? DateTime.Now.AddMonths(1);

                            var (pinCode, responseBody, requestUrl, responseStatus, requestType, requestBody) = await AssignPin(accessToken, selectedSiteId, selectedUserId, expiryDate);

                          if (!string.IsNullOrEmpty(pinCode))
                            {
                                await UpdateRecordFields(connection, recordId, selectedUserId, pinCode, requestBody, selectedSiteId, responseBody, requestUrl, responseStatus, requestType, false);
                            }
                        }
                        else
                        {
                            await UpdateRecordFields(connection, recordId, "", "", "", selectedSiteId, "user not found", "", 404, "PUT", true);
                        }
                    }
                    else
                    {
                        Notas.Log("Nenhuma requisição pendente encontrada.");
                        EventLog.WriteEntry("Nenhuma requisição pendente encontrada.");
                    }
                }
            }
        }
    }

    private static async Task<string?> GetUserByLastName(string accessToken, string lastName)
    {
        var apiUrl = $"https://clp-accept-user.my-clay.com/v1.1/sites/{selectedSiteId}/users";

        using (var httpClient = new HttpClient())
        {
            httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

            var response = await httpClient.GetAsync(apiUrl);
            var content = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                var json = JObject.Parse(content);
                var users = json["items"]?.ToObject<JArray>();

                if (users == null || !users.Any())
                {
                    Notas.Log("Nenhum utilizador encontrado.");
                    Console.WriteLine("Nenhum utilizador encontrado.");
                    return null;
                }

                foreach (var userObject in users)
                {
                    var user = userObject["user"];
                    var id = userObject["id"].ToString();
                    var userLastName = user["last_name"].ToString();

                    if (string.Equals(userLastName, lastName, StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine($"Utilizador encontrado: {user["first_name"]} {userLastName} (ID: {id})");
                        Notas.Log($"ID do utilizadores Selecionado: {id}");
                        return id;
                    }
                }

Notas.Log("Nenhum utilizador encontrado com o sobrenome correspondente.");
                Console.WriteLine("Nenhum utilizador encontrado com o sobrenome correspondente.");
                return null;
            }
            else
            {
                 Notas.Log("Erro ao buscar utilizadores: " + content);
                Console.WriteLine("Erro ao buscar utilizadores: " + content);
                return null;
            }
        }
    }

private static async Task<(string? pinCode, string responseBody, string requestUrl, int responseStatus, string requestType, string requestBody)> AssignPin(string accessToken, string siteId, string userId, DateTime expiryDate)
    {
        var apiUrl = $"https://clp-accept-user.my-clay.com/v1.1/sites/{siteId}/users/{userId}/pin";
        
        var body = new JObject
        {
            { "expiry_date", expiryDate.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") }
        };

        using (var httpClient = new HttpClient())
        {
            httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            var content = new StringContent(body.ToString(), System.Text.Encoding.UTF8, "application/json");

            var response = await httpClient.PutAsync(apiUrl, content);
            var responseBody = await response.Content.ReadAsStringAsync();

            var responseStatus = (int)response.StatusCode;

            string pinCode = null;

            if (response.IsSuccessStatusCode)
            {
                pinCode = responseBody.Trim().Trim('"') + "#";
                Notas.Log("PIN atribuído com sucesso!");
                Console.WriteLine("PIN atribuído com sucesso!");
                Console.WriteLine("PIN: " + pinCode);
            }
            else
            {
                Notas.Log("Erro ao atribuir PIN: " + responseBody);
                Console.WriteLine("Erro ao atribuir PIN: " + responseBody);
            }

            return (pinCode, responseBody, apiUrl, responseStatus, "PUT", body.ToString());
        }
    }

   private static async Task UpdateRecordFields(SqlConnection connection, string recordId, string userId, string pinCode, string requestBody, string siteId, string responseBody, string requestUrl, int responseStatus, string requestType, bool isError)
    {
        var query = @"UPDATE requestRecordsCode SET 
            requestDate = @RequestDate,
            code = @Code, 
            siteID = @SiteID,
            saltoUserID = @SaltoUserID, 
            control = 'S', 
            requestBody = @RequestBody, 
            responseBody = @ResponseBody, 
            requestUrl = @RequestURL, 
            responseStatus = @ResponseStatus, 
            requestType = @RequestType 
            WHERE recordID = @recordID";

        using (var command = new SqlCommand(query, connection))
        {
            command.Parameters.AddWithValue("@RequestDate", DateTime.Now);
            command.Parameters.AddWithValue("@Code", (object)pinCode ?? DBNull.Value);
            command.Parameters.AddWithValue("@SaltoUserID", (object)userId ?? DBNull.Value);
            command.Parameters.AddWithValue("@SiteID", siteId);
            command.Parameters.AddWithValue("@recordID", recordId);
            command.Parameters.AddWithValue("@RequestBody", (object)requestBody ?? DBNull.Value);
            command.Parameters.AddWithValue("@ResponseBody", responseBody);
            command.Parameters.AddWithValue("@RequestURL", (object)requestUrl ?? DBNull.Value);
            command.Parameters.AddWithValue("@ResponseStatus", responseStatus);
            command.Parameters.AddWithValue("@RequestType", requestType);

            await command.ExecuteNonQueryAsync();
        }
    }

    public static void Main(string[] args)
    {
        // Start the service properly
        ServiceBase.Run(new ServiceBase[] { new MyWindowsService() });
    }
}