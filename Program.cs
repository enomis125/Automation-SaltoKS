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

class Program
{
    private static string? selectedSiteId;
    private static string connectionString;
    private static string accessToken;
    private static string refreshToken;

    static async Task Main(string[] args)
    {
        connectionString = ReadConnectionStringFromFile("connectionString.txt");
        
        (accessToken, refreshToken) = await RetrieveAccessTokenAndRefreshToken();

        if (string.IsNullOrEmpty(accessToken))
        {
            Console.WriteLine("Nenhum token de acesso encontrado.");
            return;
        }
        
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        // Chamar a função para selecionar o site
        await DisplaySiteSelectionMenu(accessToken);

        // Verificar a base de dados a cada 10 segundos
        while (true)
        {
            await CheckDatabaseForPendingRequests(connectionString, accessToken, refreshToken);
            await Task.Delay(10000); // Verifica a cada 10 segundos
        }
    }

    private static async Task<(string, string)> RetrieveAccessTokenAndRefreshToken()
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

                    // Check if token is still valid
                    if (DateTime.UtcNow < tokenExpiration)
                    {
                        return (accessToken, refreshToken); // Token is valid, return it
                    }
                    else
                    {
                        // Token expired, refresh it
                        return await RefreshAccessToken(refreshToken);
                    }
                }
                else
                {
                    Console.WriteLine("No token found in the database.");
                    return (null, null);
                }
            }
        }
    }
}

    private static string ReadConnectionStringFromFile(string filePath)
    {
        return File.ReadAllText(filePath).Trim();
    }

    private static async Task<(string, string)> RefreshAccessToken(string refreshToken)
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
            { "client_secret", "" }
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

            return (newAccessToken, newRefreshToken);
        }
        else
        {
            Console.WriteLine("Error refreshing token: " + content);
            return (null, null);
        }
    }
}



private static async Task UpdateTokensInDatabase(string newAccessToken, string newRefreshToken, DateTime expirationDate)
{
    using (var connection = new SqlConnection(connectionString))
    {
        await connection.OpenAsync();
        var query = @"UPDATE requestConfig SET 
                        accessToken = @AccessToken, 
                        refreshToken = @RefreshToken, 
                        tokenExpiration = @TokenExpiration 
                      WHERE requestConfigID = 2"; // Assuming there's a single row with id=1

        using (var command = new SqlCommand(query, connection))
        {
            command.Parameters.AddWithValue("@AccessToken", newAccessToken);
            command.Parameters.AddWithValue("@RefreshToken", newRefreshToken);
            command.Parameters.AddWithValue("@TokenExpiration", expirationDate);

            await command.ExecuteNonQueryAsync();
        }
    }
}


    private static string? ExtractAuthorizationCode(string callbackUrl)
    {
        if (string.IsNullOrEmpty(callbackUrl)) return null;

        var uri = new Uri(callbackUrl);
        var query = HttpUtility.ParseQueryString(uri.Query);
        return query["code"];
    }

    private static async Task DisplaySiteSelectionMenu(string accessToken)
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
                    Console.WriteLine("Nenhum site encontrado.");
                    return;
                }

                // Selecionar o primeiro site automaticamente
                var site = sites[0];
                selectedSiteId = site["id"]?.ToString();
                Console.WriteLine($"ID do Site Selecionado: {selectedSiteId}");
            }
            else
            {
                Console.WriteLine("Erro ao buscar sites: " + content);
            }
        }
    }

    private static async Task CheckDatabaseForPendingRequests(string connectionString, string accessToken, string refreshToken)
    {
        // Check if token is still valid before making the request
        if (string.IsNullOrEmpty(accessToken))
        {
            (accessToken, refreshToken) = await RefreshAccessToken(refreshToken);
            if (string.IsNullOrEmpty(accessToken))
            {
                Console.WriteLine("Erro ao renovar o token. Saindo...");
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

                        Console.WriteLine($"Requisição pendente encontrada. recordID: {recordId}, protelRoomID: {protelRoomID}");

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
                        Console.WriteLine("Nenhuma requisição pendente encontrada.");
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
                        return id;
                    }
                }

                Console.WriteLine("Nenhum utilizador encontrado com o sobrenome correspondente.");
                return null;
            }
            else
            {
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

            Console.WriteLine("Código de Status: " + responseStatus);
            Console.WriteLine("Descrição do Status: " + response.ReasonPhrase);
            Console.WriteLine("Conteúdo da resposta: " + responseBody);

            string pinCode = null;

            if (response.IsSuccessStatusCode)
            {
                pinCode = responseBody.Trim().Trim('"') + "#";
                Console.WriteLine("PIN atribuído com sucesso!");
                Console.WriteLine("PIN: " + pinCode);
            }
            else
            {
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
}
