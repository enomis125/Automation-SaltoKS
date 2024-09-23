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

    static async Task Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        // Lê a connection string do arquivo
        string connectionString = ReadConnectionStringFromFile("connectionString.txt");

        // Obter o token de acesso
        var accessToken = await GetAccessToken();

        if (string.IsNullOrEmpty(accessToken))
        {
            Console.WriteLine("Falha ao obter o token de acesso.");
            return;
        }

        // Chamar a função para selecionar o site
        await DisplaySiteSelectionMenu(accessToken);

        // Verificar a base de dados a cada 10 segundos
        while (true)
        {
            await CheckDatabaseForPendingRequests(connectionString, accessToken);
            await Task.Delay(10000); // Verifica a cada 10 segundos
        }
    }

    private static string ReadConnectionStringFromFile(string filePath)
    {
        return File.ReadAllText(filePath).Trim();
    }

    private static async Task<string> GetAccessToken()
    {
        var clientId = "956ebbbe-785a-4948-8592-ad2b826b0e6a"; // Insira seu client_id aqui
        var redirectUri = "https://app-accept.saltoks.com/callback";
        var scope = "user_api.full_access openid profile offline_access";
        var authorizationEndpoint = "https://clp-accept-identityserver.saltoks.com/connect/authorize";
        var tokenUrl = "https://clp-accept-identityserver.saltoks.com/connect/token";

        var codeVerifier = PKCEHelper.GenerateCodeVerifier();
        var codeChallenge = PKCEHelper.GenerateCodeChallenge(codeVerifier);
        var codeChallengeMethod = "S256";

        var authorizationUrl = $"{authorizationEndpoint}?response_type=code" +
                                $"&client_id={clientId}" +
                                $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                                $"&scope={Uri.EscapeDataString(scope)}" +
                                $"&code_challenge={codeChallenge}" +
                                $"&code_challenge_method={codeChallengeMethod}";

        Console.WriteLine("A abrir o navegador para autorização...");
        Process.Start(new ProcessStartInfo
        {
            FileName = authorizationUrl,
            UseShellExecute = true
        });

        Console.WriteLine("Cole o URL completo de callback aqui:");
        var callbackUrl = Console.ReadLine();

        var authorizationCode = ExtractAuthorizationCode(callbackUrl);

        if (string.IsNullOrEmpty(authorizationCode))
        {
            Console.WriteLine("Falha ao extrair o código de autorização do URL.");
            return null;
        }

        using (var httpClient = new HttpClient())
        {
            var request = new HttpRequestMessage(HttpMethod.Post, tokenUrl);
            var parameters = new Dictionary<string, string>
            {
                { "grant_type", "authorization_code" },
                { "code", authorizationCode },
                { "redirect_uri", redirectUri },
                { "client_id", clientId },
                { "code_verifier", codeVerifier }
            };

            request.Content = new FormUrlEncodedContent(parameters);

            var response = await httpClient.SendAsync(request);
            var content = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                var json = JObject.Parse(content);
                var accessToken = json["access_token"]?.ToString();
                return accessToken;
            }
            else
            {
                Console.WriteLine("Erro ao obter token: " + content);
                return null;
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

    private static async Task CheckDatabaseForPendingRequests(string connectionString, string accessToken)
{
    using (var connection = new SqlConnection(connectionString))
    {
        await connection.OpenAsync();

        var query = "SELECT TOP 1 recordID FROM requestRecordsCode WHERE control = 'N'";
        using (var command = new SqlCommand(query, connection))
        {
            using (var reader = await command.ExecuteReaderAsync())
            {
                if (await reader.ReadAsync())
                {
                    var recordId = reader["recordID"].ToString();
                    Console.WriteLine($"Requisição pendente encontrada. recordID: {recordId}");

                    // Fechar o reader antes de realizar qualquer outra operação na mesma conexão
                    reader.Close();

                    // Chamar a função para exibir a lista de usuários e selecionar um
                    var selectedUserId = await DisplayUserSelectionMenu(accessToken);

                    if (!string.IsNullOrEmpty(selectedUserId))
                    {
                        // Definir a data de expiração do PIN
                        var expiryDate = DateTime.Now.AddMonths(1);

                        // Chamar a função AssignPin e capturar os valores
                        var (pinCode, responseBody, requestUrl, responseStatus, requestType, requestBody) = await AssignPin(accessToken, selectedSiteId, selectedUserId, expiryDate);

                        if (!string.IsNullOrEmpty(pinCode))
                        {
                            // Atualizar o campo 'control' e outros após atribuir o PIN
                            await UpdateRecordFields(connection, recordId, selectedUserId, pinCode, requestBody, selectedSiteId, responseBody, requestUrl, responseStatus, requestType, false);
                        }
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



    private static async Task<string?> DisplayUserSelectionMenu(string accessToken)
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

                // Exibir a lista de usuários
                Console.WriteLine("Selecione um utilizador para atribuir um PIN:");
                for (int i = 0; i < users.Count; i++)
                {
                    var user = users[i]["user"];
                    var id = users[i]["id"].ToString();
                    var firstName = user["first_name"].ToString();
                    var lastName = user["last_name"].ToString();
                    Console.WriteLine($"{i + 1}: {firstName} {lastName} (ID: {id})");
                }

                Console.WriteLine("Introduza o número do utilizador:");
                var choice = Console.ReadLine();

                if (int.TryParse(choice, out int index) && index > 0 && index <= users.Count)
                {
                    return users[index - 1]["id"].ToString();
                }
                else
                {
                    Console.WriteLine("Escolha inválida.");
                    return null;
                }
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

        var requestType = "PUT";
        var responseStatus = (int)response.StatusCode;

        Console.WriteLine("Código de Status: " + responseStatus);
        Console.WriteLine("Descrição do Status: " + response.ReasonPhrase);
        Console.WriteLine("Conteúdo da resposta:");
        Console.WriteLine(responseBody);

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

        return (pinCode, responseBody, apiUrl, responseStatus, requestType, body.ToString());
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
        command.Parameters.AddWithValue("@Code", pinCode);
        command.Parameters.AddWithValue("@SaltoUserID", userId);
        command.Parameters.AddWithValue("@SiteID", siteId);
        command.Parameters.AddWithValue("@recordID", recordId);
        command.Parameters.AddWithValue("@RequestBody", requestBody);
        command.Parameters.AddWithValue("@ResponseBody", responseBody);
        command.Parameters.AddWithValue("@RequestURL", requestUrl);
        command.Parameters.AddWithValue("@ResponseStatus", responseStatus);
        command.Parameters.AddWithValue("@RequestType", requestType);

        await command.ExecuteNonQueryAsync();
    }
}


}
