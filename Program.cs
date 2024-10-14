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
using Newtonsoft.Json;
using System.Net.Mail; 
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

        // Executar o script SQL de inicialização
        string scriptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "xsetup.sql");
        ExecuteSqlScript(scriptPath, connectionString);
        
        timer = new Timer(async state => await DoWork(), null, TimeSpan.Zero, TimeSpan.FromSeconds(30));
        Notas.Log("Service started successfully.");
    }
    catch (Exception ex)
    {
        Notas.Log("Error during service start: " + ex.Message);

        SendErrorEmail("Erro ao iniciar o serviço SysConector", ex.Message);
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

            string message = "Não foi encontrado nenhum token.";
            SendErrorEmail("Erro: Nenhum token encontrado", message);

            return;
        }

        // Atualizar timeToAlive
        await UpdateTimeToAlive();

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

        string message = "Message: " + ex.Message;
        SendErrorEmail("Erro ao iniciar o serviço SysConector", message);

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

        string message = "\nMessage: " + ex.Message;
        SendErrorEmail("Erro ao ler a connection string", message);
    }
}

private void ExecuteSqlScript(string scriptPath, string connectionString)
{
    try
    {
        // Lê o script SQL do arquivo
        string script = File.ReadAllText(scriptPath);

        // Conecta-se ao banco de dados e executa o script
        using (SqlConnection connection = new SqlConnection(connectionString))
        {
            connection.Open();
            using (SqlCommand command = new SqlCommand(script, connection))
            {
                command.ExecuteNonQuery();
            }
        }

        Notas.Log("Script SQL executado com sucesso.");
    }
    catch (Exception ex)
    {
        Notas.Log("Erro ao executar o script SQL: " + ex.Message);
        throw new Exception("Erro ao executar o script SQL: " + ex.Message);

        string message = "Message: " + ex.Message;
        SendErrorEmail("Erro ao executar o script SQL", message);
    }
}

//atualiza o timeToAlive de 30 em 30 seg para saber se o programa está a funcionar ou não
private async Task UpdateTimeToAlive()
{
    try
    {
        using (SqlConnection connection = new SqlConnection(connectionString))
        {
            await connection.OpenAsync();

            string query = "UPDATE [proteluser].[xsetup] SET xvalue = @currentTime WHERE xsection = 'SysConector' AND xkey = 'timeToAlive'";
            using (SqlCommand command = new SqlCommand(query, connection))
            {
                command.Parameters.AddWithValue("@currentTime", DateTime.UtcNow.ToString("o")); // Formato ISO 8601
                await command.ExecuteNonQueryAsync();
            }
        }

        Notas.Log("timeToAlive atualizado com sucesso.");
    }
    catch (Exception ex)
    {
        Notas.Log("Erro ao atualizar timeToAlive: " + ex.Message);
        EventLog.WriteEntry("Erro ao atualizar timeToAlive: " + ex.Message, EventLogEntryType.Error);
    }
}

private static async Task<(string tokenUrl, string clientId, string apiUrl, string supportEmail, string noReplyEmail, string noReplyPassword, int sendingPort, string sendingServer)> GetSysConectorSettingsFromDatabase(string connectionString)
{
    string tokenUrl = null;
    string clientId = null;
    string apiUrl = null;
    string supportEmail = null;
    string noReplyEmail = null;
    string noReplyPassword = null;
    int sendingPort = 0;
    string sendingServer = null;

    try
    {
        using (SqlConnection connection = new SqlConnection(connectionString))
        {
            await connection.OpenAsync();

            // Consulta para buscar os valores da tabela xsetup
            string query = @"
                SELECT xkey, xvalue 
                FROM [proteluser].[xsetup] 
                WHERE xsection = 'SysConector' 
                AND xkey IN ('tokenURL', 'clientId', 'apiUrl', 'supportEmail', 'noReplyEmail', 'noReplyPassword', 'sendingPort', 'sendingServer')";

            using (SqlCommand command = new SqlCommand(query, connection))
            {
                using (SqlDataReader reader = await command.ExecuteReaderAsync())
                {
                    // Loop para obter os valores retornados
                    while (await reader.ReadAsync())
                    {
                        string key = reader["xkey"].ToString();
                        string value = reader["xvalue"].ToString();

                        // Mapear os valores de acordo com o xkey
                        switch (key)
                        {
                            case "tokenURL":
                                tokenUrl = value;
                                break;
                            case "clientId":
                                clientId = value;
                                break;
                            case "apiUrl":
                                apiUrl = value;
                                break;
                            case "supportEmail":
                                supportEmail = value;
                                break;
                            case "noReplyEmail":
                                noReplyEmail = value;
                                break;
                            case "noReplyPassword":
                                noReplyPassword = value;
                                break;
                            case "sendingPort":
                                sendingPort = int.Parse(value);
                                break;
                            case "sendingServer":
                                sendingServer = value;
                                break;
                        }
                    }
                }
            }
        }
    }
    catch (Exception ex)
    {
        Notas.Log("Error retrieving SysConector settings from database: " + ex.Message);
        // Aqui você pode decidir enviar um email ou logar o erro
    }

    return (tokenUrl, clientId, apiUrl, supportEmail, noReplyEmail, noReplyPassword, sendingPort, sendingServer);
}

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

                            string message = "Nenhum token encontrado na base de dados.";
                                SendErrorEmail("Erro no Serviço: Falha ao encontrar token", message);

                            return (null, null);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Notas.Log("Error during token refresh: " + ex.Message);

            string message = "Message: " + ex.Message;
            SendErrorEmail("Erro durante o refresh token", message);

            EventLog.WriteEntry("Error retrieving token: " + ex.Message, EventLogEntryType.Error);
            return (null, null);
        }
    }

    // Refreshes the access token
    private async Task<(string, string)> RefreshAccessToken(string refreshToken)
{
    try
    {
        // Obtém a string de conexão da base de dados
        string connectionString = ReadConnectionStringFromFile("connectionString.txt");

        // Busca os valores apiUrl, tokenUrl e clientId na base de dados
        var (tokenUrl, clientId, apiUrlBase, supportEmail, noReplyEmail, noReplyPassword, sendingPort, sendingServer) = await GetSysConectorSettingsFromDatabase(connectionString);

        if (string.IsNullOrEmpty(tokenUrl) || string.IsNullOrEmpty(clientId))
        {
            Notas.Log("Erro: tokenUrl ou clientId não encontrados na base de dados.");
            return (null, null); // Retorna com erro
        }

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

                Notas.Log("Refreshing access token."); 
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
                          WHERE requestConfigID = 1";

            using (var command = new SqlCommand(query, connection))
            {
                command.Parameters.AddWithValue("@AccessToken", newAccessToken);
                command.Parameters.AddWithValue("@RefreshToken", newRefreshToken);
                command.Parameters.AddWithValue("@TokenExpiration", expirationDate);

                await command.ExecuteNonQueryAsync();
            }
        }
    }

private static async Task SendEmailWithPin(string email, string pinCode, string guestName)
{
    try
    {
        // Obtém a string de conexão da base de dados
        string connectionString = ReadConnectionStringFromFile("connectionString.txt");

        // Busca os emails e senhas na base de dados
        var (tokenUrl, clientId, apiUrlBase, supportEmail, noReplyEmail, noReplyPassword, sendingPort, sendingServer) = await GetSysConectorSettingsFromDatabase(connectionString);

        // Verifica se o email ou senha estão vazios
        if (string.IsNullOrEmpty(noReplyEmail) || string.IsNullOrEmpty(noReplyPassword))
        {
            Notas.Log("Erro: noReplyEmail ou noReplyPassword não encontrados na base de dados.");
            return; // Adicionando um retorno para evitar envio de e-mail sem credenciais
        }

        var smtpClient = new SmtpClient(sendingServer) // Defina o servidor SMTP
        {
            Port = (sendingPort),
            Credentials = new System.Net.NetworkCredential(noReplyEmail, noReplyPassword), // Usa o noReplyEmail e noReplyPassword da base de dados
            EnableSsl = true,
        };

        string subject = "Seu código PIN de acesso";
        string body = $"Olá {guestName},\n\n" +
                      $"Aqui está o seu código PIN de acesso: {pinCode}\n\n" +
                      "Este PIN pode ser usado para acessar as instalações até a data de expiração.\n\n" +
                      "Atenciosamente,\nEquipe de Suporte";

        var mailMessage = new MailMessage
        {
            From = new MailAddress(noReplyEmail), // Usa o noReplyEmail da base de dados
            Subject = subject,
            Body = body,
            IsBodyHtml = false,
        };
        mailMessage.To.Add(email); // Email do hóspede

        await smtpClient.SendMailAsync(mailMessage);
        Notas.Log($"Email enviado com sucesso para: {email}");
    }
    catch (Exception ex)
    {
        Notas.Log($"Falha ao enviar email para {email}. Erro: {ex.Message}");

        string message = "Falha ao enviar email para: " + email + "\nErro: " + ex.Message;
        await SendErrorEmail("Erro ao enviar email com pin para hóspede", message); // Envia o email de erro
    }
}


public static async Task SendErrorEmail(string subject, string message)
{
    try
    {
        // Obtém a string de conexão da base de dados
        string connectionString = ReadConnectionStringFromFile("connectionString.txt");

        // Busca os emails e senhas na base de dados
        var (tokenUrl, clientId, apiUrlBase, supportEmail, noReplyEmail, noReplyPassword, sendingPort, sendingServer) = await GetSysConectorSettingsFromDatabase(connectionString);

        // Se o email não for encontrado, usa um email padrão ou lança uma exceção
        if (string.IsNullOrEmpty(supportEmail))
        {
            Notas.Log("Erro: supportEmail não encontrado na base de dados.");
            return; // Adicionando um retorno para evitar envio de e-mail
        }

        MailMessage mail = new MailMessage();
        SmtpClient smtpServer = new SmtpClient(sendingServer);

        mail.From = new MailAddress(noReplyEmail); // Usa o noReplyEmail da base de dados
        mail.To.Add(supportEmail); // Usa o supportEmail da base de dados
        mail.Subject = subject;
        mail.Body = message;

        smtpServer.Port = (sendingPort); 
        smtpServer.Credentials = new System.Net.NetworkCredential(noReplyEmail, noReplyPassword);
        smtpServer.EnableSsl = true;

        smtpServer.Send(mail);
    }
    catch (Exception ex)
    {
        Console.WriteLine("Erro ao enviar email: " + ex.Message);
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
    // Obtém a string de conexão da base de dados
    string connectionString = ReadConnectionStringFromFile("connectionString.txt");

    // Busca os valores tokenUrl, clientId e apiUrl na base de dados
    var (tokenUrl, clientId, apiUrlBase, supportEmail, noReplyEmail, noReplyPassword, sendingPort, sendingServer) = await GetSysConectorSettingsFromDatabase(connectionString);

    if (string.IsNullOrEmpty(apiUrlBase))
    {
        Notas.Log("Erro: apiUrl não encontrado na base de dados.");
        return; // Sai da função se não conseguir obter o apiUrl
    }

    // Parte variável da URL que você precisa adicionar
    string siteEndpoint = "sites/"; // ou outro endpoint que você precisa acessar
    var fullApiUrl = new Uri(new Uri(apiUrlBase), siteEndpoint).ToString(); // Combina a base com o endpoint

    using (var httpClient = new HttpClient())
    {
        httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

        var response = await httpClient.GetAsync(fullApiUrl);
        var content = await response.Content.ReadAsStringAsync();

        if (response.IsSuccessStatusCode)
        {
            var json = JObject.Parse(content);
            var sites = json["items"]?.ToObject<JArray>();

            if (sites == null || !sites.Any())
            {
                Notas.Log("Nenhum site encontrado.");
                return;
            }

            // Selecionar o primeiro site automaticamente
            var site = sites[0];
            selectedSiteId = site["id"]?.ToString();
            Notas.Log($"ID do Site Selecionado: {selectedSiteId}");
        }
        else
        {
            Notas.Log("Erro ao buscar sites: " + content);
            SendErrorEmail("Erro no Serviço: Falha ao buscar sites", content);
        }
    }
}

  private static async Task CheckDatabaseForPendingRequests(string connectionString, string accessToken, string refreshToken)
{
    using (var connection = new SqlConnection(connectionString))
    {
        await connection.OpenAsync();

        // Primeiro, vamos tratar o caso onde control = 'N'
        var controlQuery = "SELECT recordID, protelGuestName, protelGuestEmail, protelRoomID, protelValidFrom, protelValidUntil, protelReservationID FROM requestRecordsCode WHERE control = 'N'";

        using (var controlCommand = new SqlCommand(controlQuery, connection))
        {
            using (var controlReader = await controlCommand.ExecuteReaderAsync())
            {
                while (await controlReader.ReadAsync())
                {
                    var recordId = controlReader["recordID"].ToString();
                    var protelGuestName = controlReader["protelGuestName"].ToString();
                    var protelGuestEmail = controlReader["protelGuestEmail"].ToString();
                    var protelRoomID = controlReader["protelRoomID"].ToString();
                    var protelReservationID = controlReader["protelReservationID"].ToString();

                    var protelValidFrom = controlReader["protelValidFrom"] != DBNull.Value
                        ? Convert.ToDateTime(controlReader["protelValidFrom"])
                        : DateTime.MinValue;

                    var protelValidUntil = controlReader["protelValidUntil"] != DBNull.Value
                        ? Convert.ToDateTime(controlReader["protelValidUntil"])
                        : DateTime.MinValue;

                    Console.WriteLine($"Requisição pendente encontrada. recordID: {recordId}, Nome: {protelGuestName}, Email: {protelGuestEmail}, ReservationID: {protelRoomID}, ExpiryDate: {protelValidUntil}");

                    try
                    {
                        // Criar um novo usuário, passando também o protelRoomID
                        var newUserId = await CreateNewUser(accessToken, protelGuestName, protelGuestEmail, protelReservationID, protelRoomID, protelValidFrom, protelValidUntil);

                        if (!string.IsNullOrEmpty(newUserId))
                        {
                            // Passar o expiryDate recuperado da base de dados
                            var (pinCode, responseBody, requestUrl, responseStatus, requestType, requestBody) = await AssignPin(accessToken, selectedSiteId, newUserId, protelValidUntil, protelGuestEmail, protelGuestName);

                            if (!string.IsNullOrEmpty(pinCode))
                            {
                                // Atualizar o campo 'control' e outros após atribuir o PIN
                                await UpdateRecordFields(connection, recordId, newUserId, pinCode, requestBody, selectedSiteId, responseBody, requestUrl, responseStatus, requestType);
                            }
                            else
                            {
                                // Registrar o erro no banco de dados se a atribuição do PIN falhar
                                Console.WriteLine("Erro: Atribuição de PIN falhou, registrando no banco de dados...");

                                // Construa o corpo da mensagem
                                string message = "RecordID: " + recordId + "\nResponse Body: " + responseBody;
                                SendErrorEmail("Erro no Serviço: Falha ao atribuir pin", message);
                                
                                await LogErrorToDatabase(
                                    connection, recordId, selectedSiteId, newUserId, requestBody, responseBody, requestUrl, responseStatus, requestType);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // Se ocorrer algum erro durante o processo, registrar o erro no banco e continuar para o próximo
                        Console.WriteLine($"Erro ao processar o recordID {recordId}: {ex.Message}. Registrando o erro e avançando para o próximo registro...");
                        await LogErrorToDatabase(
                            connection, recordId, selectedSiteId, null, null, ex.Message, null, 500, "Erro Interno");

                        continue; // Avançar para o próximo registro
                    }
                }

                // Query para buscar os registros onde 'sendPinAgain' é 'S'
        var query = "SELECT recordID, protelGuestName, protelGuestEmail, code FROM requestRecordsCode WHERE sendPinAgain = 'S'";

        using (var command = new SqlCommand(query, connection))
        {
            using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    var recordId = reader["recordID"].ToString();
                    var guestName = reader["protelGuestName"].ToString();
                    var guestEmail = reader["protelGuestEmail"].ToString();
                    var pinCode = reader["code"].ToString();

                    Console.WriteLine($"Enviando o PIN novamente. recordID: {recordId}, Nome: {guestName}, Email: {guestEmail}");

                    try
                    {
                        // Enviar o e-mail com o código PIN do campo 'code'
                        await SendEmailWithPin(guestEmail, pinCode, guestName);

                        // Atualizar o campo 'sendPinAgain' para 'N' após o envio do PIN
                        await UpdateSendPinAgainStatus(connection, recordId);
                    }
                    catch (Exception ex)
                    {
                        // Registrar o erro no banco de dados
                        Console.WriteLine($"Erro ao enviar email para {guestEmail}. Erro: {ex.Message}");
                        await LogErrorToDatabase(connection, recordId, selectedSiteId, null, null, ex.Message, null, 500, "Erro Interno ao enviar 2 via de pin");
                    }
                }
            }
        }
            }
        }

        // Agora, vamos tratar o caso onde deleted = 'N'
        var deletedQuery = "SELECT recordID, protelGuestName, protelGuestEmail, protelRoomID, protelValidFrom, protelValidUntil, protelReservationID FROM requestRecordsCode WHERE deleted = 'N'";

        using (var deletedCommand = new SqlCommand(deletedQuery, connection))
        {
            using (var deletedReader = await deletedCommand.ExecuteReaderAsync())
            {
                bool found = false; // Para verificar se há registros

                while (await deletedReader.ReadAsync()) // Lê todos os registros onde deleted = 'N'
                {
                    found = true; // Indica que encontramos pelo menos um registro
                    var recordId = deletedReader["recordID"].ToString();
                    var protelGuestName = deletedReader["protelGuestName"].ToString();
                    var protelGuestEmail = deletedReader["protelGuestEmail"].ToString();
                    var protelRoomID = deletedReader["protelRoomID"].ToString();
                    var protelReservationID = deletedReader["protelReservationID"].ToString();

                    var protelValidFrom = deletedReader["protelValidFrom"] != DBNull.Value
                        ? Convert.ToDateTime(deletedReader["protelValidFrom"])
                        : DateTime.MinValue;

                    var protelValidUntil = deletedReader["protelValidUntil"] != DBNull.Value
                        ? Convert.ToDateTime(deletedReader["protelValidUntil"])
                        : DateTime.MinValue;

                    Console.WriteLine($"Requisição pendente encontrada. recordID: {recordId}, Nome: {protelGuestName}, Email: {protelGuestEmail}, ReservationID: {protelRoomID}, ExpiryDate: {protelValidUntil}");

                    // Chamar a função para obter os grupos de acesso
                    await GetAccessGroups(accessToken, recordId, protelGuestName, protelGuestEmail, protelRoomID, protelReservationID, protelValidFrom, protelValidUntil, connection);
                }

                if (!found)
                {
                    Console.WriteLine("Nenhuma requisição pendente encontrada para deleted = 'N'.");
                }
            }
        }
    }
}

private static async Task UpdateSendPinAgainStatus(SqlConnection connection, string recordId)
{
    var updateQuery = "UPDATE requestRecordsCode SET sendPinAgain = 'N' WHERE recordID = @recordID";

    using (var updateCommand = new SqlCommand(updateQuery, connection))
    {
        updateCommand.Parameters.AddWithValue("@recordID", recordId);

        await updateCommand.ExecuteNonQueryAsync();
        Console.WriteLine($"Campo sendPinAgain atualizado para 'N' para o recordID: {recordId}");
    }
}


private static async Task GetAccessGroups(string accessToken, string recordId, string protelGuestName, string protelGuestEmail, string protelRoomID, string protelReservationID, DateTime protelValidFrom, DateTime protelValidUntil, SqlConnection connection)
{
    // Verifica se protelValidUntil é anterior à data atual
    if (protelValidUntil >= DateTime.UtcNow) // Usa UTC para comparação correta
    {
        Notas.Log($"A data de validade {protelValidUntil} ainda não expirou para recordID {recordId}. Não verificando os access groups.");
        return; // Sai do método se a data de validade ainda não tiver expirado
    }

    // Obtém a string de conexão da base de dados
    string connectionString = ReadConnectionStringFromFile("connectionString.txt");

    // Busca os valores apiUrl, tokenUrl e clientId na base de dados
    var (tokenUrl, clientId, apiUrlBase, supportEmail, noReplyEmail, noReplyPassword, sendingPort, sendingServer) = await GetSysConectorSettingsFromDatabase(connectionString);

    if (string.IsNullOrEmpty(apiUrlBase))
    {
        Notas.Log("Erro: apiUrl não encontrado na base de dados.");
        return; // Sai do método se não conseguir obter o apiUrl
    }

    // Parte variável da URL que você precisa adicionar
    var apiUrl = $"{apiUrlBase}sites/{selectedSiteId}/access_groups";

    // Chamada à API dos access groups
    using (var httpClient = new HttpClient())
    {
        httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

        var response = await httpClient.GetAsync(apiUrl);

        if (response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync();

            try
            {
                var jsonResponse = JObject.Parse(responseBody);
                var accessGroups = jsonResponse["items"];

                if (accessGroups != null)
                {
                    bool groupFound = false;
                    string accessGroupId = null; // Para armazenar o ID do access group encontrado

                    // Itera sobre os access groups para encontrar um correspondente ao protelReservationID
                    foreach (var group in accessGroups)
                    {
                        var customerReference = group["customer_reference"]?.ToString();
                        accessGroupId = group["id"]?.ToString(); // Captura o ID do access group

                        if (!string.IsNullOrEmpty(customerReference))
                        {
                            // Verifica se o customer_reference é exatamente igual ao pmsReservation: <protelReservationID>
                            var expectedReference = $"pmsReservation: {protelReservationID}";
                            if (customerReference.Trim() == expectedReference)
                            {
                                Notas.Log($"Access group encontrado para recordID {recordId}: {customerReference}");
                                groupFound = true;
                                break; // Interrompe a iteração já que encontrou o grupo compatível
                            }
                        }
                    }

                    if (groupFound && accessGroupId != null)
                    {
                        // Chamar a API para deletar o access group encontrado
                        var deleteUrl = $"{apiUrlBase}sites/{selectedSiteId}/access_groups/{accessGroupId}";

                        var deleteResponse = await httpClient.DeleteAsync(deleteUrl);

                        if (deleteResponse.IsSuccessStatusCode)
                        {
                            Notas.Log($"Access group com ID {accessGroupId} deletado com sucesso.");

                            var query = @"UPDATE requestRecordsCode SET 
                                          deleted = 'S'
                                          WHERE recordID = @recordID";

                            using (var command = new SqlCommand(query, connection))
                            {
                                command.Parameters.AddWithValue("@recordID", recordId);

                                try
                                {
                                    await command.ExecuteNonQueryAsync();
                                    Notas.Log($"Registro com recordID {recordId} atualizado com sucesso.");
                                }
                                catch (Exception ex)
                                {
                                    Notas.Log($"Erro ao atualizar o registro com recordID {recordId}: {ex.Message}");

                                    // Construa o corpo da mensagem
                                    string message = "RecordID: " + recordId + "\nMessage: " + ex.Message;
                                    SendErrorEmail("Erro ao atualizar o registro com recordID", message);
                                }
                            }

                            // Agora, vamos procurar o usuário associado ao access group
                            await DeleteUserByEmail(accessToken, protelGuestEmail);
                        }
                        else
                        {
                            Notas.Log($"Erro ao apagar o access group com ID {accessGroupId}: {deleteResponse.StatusCode}");

                            // Construa o corpo da mensagem
                            string message = "Access Group ID: " + accessGroupId + "\nMessage: " + deleteResponse.StatusCode;
                            SendErrorEmail("Erro ao apagar o access group", message);
                        }
                    }
                    else
                    {
                        Notas.Log($"Nenhum access group encontrado para recordID {recordId} com o ReservationID {protelReservationID}");
                    }
                }
            }
            catch (Exception ex)
            {
                Notas.Log($"Erro ao processar a resposta JSON: {ex.Message}");

                // Construa o corpo da mensagem
                string message = "Resposta JSON:\n" + ex.Message;
                SendErrorEmail("Erro ao processar a resposta JSON:", message);
            }
        }
        else
        {
            Notas.Log($"Erro ao chamar a API dos access groups: {response.StatusCode}");

            // Construa o corpo da mensagem
            string message = "Status Code: " + response.StatusCode;
            SendErrorEmail("Erro ao chamar a API dos access groups", message);
        }
    }
}

private static async Task DeleteUserByEmail(string accessToken, string email)
{
    // Obtém a string de conexão da base de dados
    string connectionString = ReadConnectionStringFromFile("connectionString.txt");

    // Busca os valores apiUrl, tokenUrl e clientId na base de dados
    var (tokenUrl, clientId, apiUrlBase, supportEmail, noReplyEmail, noReplyPassword, sendingPort, sendingServer) = await GetSysConectorSettingsFromDatabase(connectionString);

    if (string.IsNullOrEmpty(apiUrlBase))
    {
        Notas.Log("Erro: apiUrl não encontrado na base de dados.");
        return; // Sai do método se não conseguir obter o apiUrl
    }

    // URL da API de usuários
    var apiUrl = $"{apiUrlBase}sites/{selectedSiteId}/users";

    using (var httpClient = new HttpClient())
    {
        httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

        // Chamada para obter todos os usuários
        var response = await httpClient.GetAsync(apiUrl);

        if (response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync();

            try
            {
                var jsonResponse = JObject.Parse(responseBody);
                var users = jsonResponse["items"];

                if (users != null)
                {
                    string userIdToDelete = null;

                    // Itera sobre os usuários para encontrar o que corresponde ao email
                    foreach (var userItem in users)
                    {
                        var userEmail = userItem["user"]["email"]?.ToString(); // Obtém o email do usuário
                        var userId = userItem["id"]?.ToString(); // Obtém o ID do nível superior do item

                        if (!string.IsNullOrEmpty(userEmail) && userEmail.Equals(email, StringComparison.OrdinalIgnoreCase))
                        {
                            userIdToDelete = userId; // Armazena o ID do usuário
                            Notas.Log($"Usuário encontrado: {userEmail} com ID: {userIdToDelete}");
                            break; // Interrompe a iteração após encontrar o usuário
                        }
                    }

                    // Se um usuário foi encontrado, chamamos a API para deletá-lo
                    if (userIdToDelete != null)
                    {
                        var deleteUrl = $"{apiUrlBase}sites/{selectedSiteId}/users/{userIdToDelete}";

                        var deleteResponse = await httpClient.DeleteAsync(deleteUrl);

                        if (deleteResponse.IsSuccessStatusCode)
                        {
                            Notas.Log($"Utilizador com ID {userIdToDelete} apagado com sucesso.");
                        }
                        else
                        {
                            Notas.Log($"Erro ao apagar o usuário com ID {userIdToDelete}: {deleteResponse.StatusCode}");

                            // Construa o corpo da mensagem
                            string message = "Utilizador ID: " + userIdToDelete + "\nStatus Code: " + deleteResponse.StatusCode;
                            SendErrorEmail("Erro ao apagar o utilizador", message);
                        }
                    }
                    else
                    {
                        Notas.Log($"Nenhum utilizador encontrado com o email: {email}");
                    }
                }
            }
            catch (Exception ex)
            {
                Notas.Log($"Erro ao processar a resposta JSON dos utilizadores: {ex.Message}");

                // Construa o corpo da mensagem
                string message = "Resposta JSON:\n" + ex.Message;
                SendErrorEmail("Erro ao processar a resposta JSON dos utilizadores:", message);
            }
        }
        else
        {
            Notas.Log($"Erro ao chamar a API dos utilizadores: {response.StatusCode}");

            // Construa o corpo da mensagem
            string message = "Status Code:\n" + response.StatusCode;
            SendErrorEmail("Erro ao chamar a API dos utilizadores:", message);
        }
    }
}

   private static async Task<string?> CreateNewUser(string accessToken, string guestName, string guestEmail, string protelReservationID, string protelRoomID, DateTime protelValidFrom, DateTime protelValidUntil)
{
    // Obtém a string de conexão da base de dados
    string connectionString = ReadConnectionStringFromFile("connectionString.txt");

    // Busca os valores apiUrl, tokenUrl e clientId na base de dados
    var (tokenUrl, clientId, apiUrlBase, supportEmail, noReplyEmail, noReplyPassword, sendingPort, sendingServer) = await GetSysConectorSettingsFromDatabase(connectionString);

    if (string.IsNullOrEmpty(apiUrlBase))
    {
        Notas.Log("Erro: apiUrl não encontrado na base de dados.");
        return null; // Sai do método se não conseguir obter o apiUrl
    }

    // Monta a URL da API de criação de usuários
    var apiUrl = $"{apiUrlBase}sites/{selectedSiteId}/users";

    var body = new JObject
    {
        { "first_name", guestName },
        { "last_name", "Guest" },
        { "email", guestEmail },
        { "role_ids", new JArray { "8f6081a3-80fb-11e8-a892-000d3a221c5b" } }, // Exemplo de ID de role
        { "alias", "string" },
        { "toggle_easy_office_mode", true },
        { "toggle_manual_office_mode", true },
        { "blocked", false },
        { "override_privacy_mode", true },
        { "use_pin", true }
    };

    using (var httpClient = new HttpClient())
    {
        httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        var content = new StringContent(body.ToString(), System.Text.Encoding.UTF8, "application/json");

        var response = await httpClient.PostAsync(apiUrl, content);
        var responseBody = await response.Content.ReadAsStringAsync();

        // Log the entire response for debugging
        Notas.Log("Resposta da API ao criar usuário: ");
        Notas.Log(responseBody);

        if (response.IsSuccessStatusCode)
        {
            try
            {
                var json = JObject.Parse(responseBody);
                
                // Obter o ID interno do usuário
                var userId = json["id"]?.ToString();
                var internalUserId = json["user"]?["id"]?.ToString();
                if (string.IsNullOrEmpty(userId))
                {
                    Notas.Log("Erro: Não foi possível obter o 'user.id' da resposta.");

                    // Construa o corpo da mensagem
                    string message = "Ao criar um novo utilizador, não foi possivel retirar o user.id do JSON.";
                    SendErrorEmail("Erro: Não foi possível obter o 'user.id' da resposta", message);

                    return null;
                }

                Notas.Log($"Usuário criado com sucesso! ID do usuário: {userId}");

                // Criar um novo grupo de acesso
                var accessGroupId = await CreateAccessGroup(accessToken, protelReservationID, protelValidFrom, protelValidUntil);
                if (!string.IsNullOrEmpty(accessGroupId))
                {
                    Notas.Log($"Grupo de acesso criado com sucesso! ID do grupo: {accessGroupId}");

                    // Adicionar o usuário ao grupo de acesso
                    var added = await AddUserToAccessGroup(accessToken, accessGroupId, internalUserId);
                    if (added)
                    {
                        Notas.Log($"Usuário {internalUserId} adicionado ao grupo de acesso {accessGroupId} com sucesso.");

                        // Obter o ID da fechadura correspondente ao protelRoomID
                        var lockId = await GetLockId(accessToken, selectedSiteId, protelRoomID);
                        if (!string.IsNullOrEmpty(lockId))
                        {
                            // Associar a fechadura ao grupo de acesso
                            var lockAssociated = await AssociateLockToAccessGroup(accessToken, accessGroupId, lockId);
                            if (lockAssociated)
                            {
                                Notas.Log($"Fechadura {lockId} associada ao grupo de acesso {accessGroupId} com sucesso.");
                            }
                            else
                            {
                                Notas.Log($"Falha ao associar a fechadura {lockId} ao grupo de acesso {accessGroupId}.");

                                // Construa o corpo da mensagem
                                string message = "Falha ao associar a fechadura " + lockId + " ao grupo de acesso " + accessGroupId;
                                SendErrorEmail("Erro ao associar a fechadura ao access group", message);
                            }
                        }
                        else
                        {
                            Notas.Log($"Nenhuma fechadura encontrada para associar ao grupo de acesso {accessGroupId}.");

                            // Construa o corpo da mensagem
                            string message = "Nenhuma fechadura encontrada para associar ao grupo de acesso " + accessGroupId;
                            SendErrorEmail("Erro: Nenhuma fechadura encontrada para associar", message);
                        }
                    }
                    else
                    {
                        Notas.Log($"Falha ao adicionar o usuário {userId} ao grupo de acesso {accessGroupId}.");

                        // Construa o corpo da mensagem
                        string message = "Falha ao adicionar o utilizador " + userId + " ao grupo de acesso " + accessGroupId;
                        SendErrorEmail("Erro ao adicionar o utilizador ao access group", message);
                    }
                }

                return userId; // Retorna o ID do usuário criado
            }
            catch (Exception ex)
            {
                Notas.Log($"Erro ao processar a resposta JSON: {ex.Message}");

                // Construa o corpo da mensagem
                string message = "Resposta JSON: " + ex.Message;
                SendErrorEmail("Erro ao processar a resposta JSON", message);

                return null;
            }
        }
        else
        {
            Notas.Log($"Erro ao criar usuário: {responseBody}");

            // Construa o corpo da mensagem
            string message = "Response Body: " + responseBody;
            SendErrorEmail("Erro ao criar utilizador", message);

            return null;
        }
    }
}

private static async Task<string?> CreateAccessGroup(string accessToken, string protelReservationID, DateTime protelValidFrom, DateTime protelValidUntil)
{
    // Obtém a string de conexão da base de dados
    string connectionString = ReadConnectionStringFromFile("connectionString.txt");

    // Busca os valores apiUrl, tokenUrl e clientId na base de dados
    var (tokenUrl, clientId, apiUrlBase, supportEmail, noReplyEmail, noReplyPassword, sendingPort, sendingServer) = await GetSysConectorSettingsFromDatabase(connectionString);

    if (string.IsNullOrEmpty(apiUrlBase))
    {
        Notas.Log("Erro: apiUrl não encontrado na base de dados.");
        return null; // Sai do método se não conseguir obter o apiUrl
    }

    // Monta a URL da API de criação de grupos de acesso
    var apiUrl = $"{apiUrlBase}sites/{selectedSiteId}/access_groups";
    
    var body = new JObject
    {
        { "customer_reference", "pmsReservation: " + protelReservationID }
    };

    using (var httpClient = new HttpClient())
    {
        httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        var content = new StringContent(body.ToString(), System.Text.Encoding.UTF8, "application/json");

        var response = await httpClient.PostAsync(apiUrl, content);
        var responseBody = await response.Content.ReadAsStringAsync();

        // Log the entire response for debugging
        Notas.Log("Resposta da API ao criar grupo de acesso: ");
        Notas.Log(responseBody);

        if (response.IsSuccessStatusCode)
        {
            try
            {
                var json = JObject.Parse(responseBody);
                var accessGroupId = json["id"]?.ToString(); // Assume que o ID do grupo está neste campo

                if (string.IsNullOrEmpty(accessGroupId))
                {
                    Notas.Log("Erro: Não foi possível obter o 'id' do grupo de acesso.");
                    
                    // Construa o corpo da mensagem
                    string message = "Ao criar um novo grupo de acesso, não foi possível obter o id do JSON.";
                    SendErrorEmail("Erro ao criar grupo de acesso", message);

                    return null;
                }

                // Cria o agendamento de tempo para o grupo de acesso
                await CreateTimeSchedule(accessToken, selectedSiteId, accessGroupId, protelValidFrom, protelValidUntil);

                Notas.Log($"Grupo de acesso criado com sucesso! ID do grupo: {accessGroupId}");
                return accessGroupId;
            }
            catch (Exception ex)
            {
                Notas.Log($"Erro ao processar a resposta JSON: {ex.Message}");

                // Construa o corpo da mensagem
                string message = "Resposta JSON: " + ex.Message;
                SendErrorEmail("Erro ao processar a resposta JSON ao criar grupo de acesso", message);

                return null;
            }
        }
        else
        {
            Notas.Log($"Erro ao criar grupo de acesso: {responseBody}");

            // Construa o corpo da mensagem
            string message = "Response Body: " + responseBody;
            SendErrorEmail("Erro ao criar grupo de acesso", message);

            return null;
        }
    }
}

private static async Task CreateTimeSchedule(string accessToken, string selectedSiteId, string accessGroupId, DateTime protelValidFrom, DateTime protelValidUntil)
{
    // Obtém a string de conexão da base de dados
    string connectionString = ReadConnectionStringFromFile("connectionString.txt");

    // Busca os valores apiUrl, tokenUrl e clientId na base de dados
    var (tokenUrl, clientId, apiUrlBase, supportEmail, noReplyEmail, noReplyPassword, sendingPort, sendingServer) = await GetSysConectorSettingsFromDatabase(connectionString);

    if (string.IsNullOrEmpty(apiUrlBase))
    {
        Notas.Log("Erro: apiUrl não encontrado na base de dados.");
        return; // Sai do método se não conseguir obter o apiUrl
    }

    // Monta a URL da API de criação de time schedule
    var apiUrl = $"{apiUrlBase}sites/{selectedSiteId}/access_groups/{accessGroupId}/time_schedules";

    var body = new JObject
    {
        {"monday", true},
        {"tuesday", true},
        {"wednesday", true},
        {"thursday", true},
        {"friday", true},
        {"saturday", true},
        {"sunday", true},
        {"start_time", "00:00:00"},
        {"end_time", "23:59:59"},
        { "start_date", protelValidFrom.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") },
        { "end_date", protelValidUntil.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") }
    };

    using (var httpClient = new HttpClient())
    {
        httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        var content = new StringContent(body.ToString(), System.Text.Encoding.UTF8, "application/json");

        var response = await httpClient.PostAsync(apiUrl, content);
        var responseBody = await response.Content.ReadAsStringAsync();

        if (response.IsSuccessStatusCode)
        {
            Notas.Log($"Horário criado com sucesso! Resposta: {responseBody}");
        }
        else
        {
            Notas.Log($"Erro ao criar horário: {responseBody}");

            // Construa o corpo da mensagem
            string message = "Ocorreu um erro ao criar a timeline de check-in e check-out para o access group.\nResponse Body: " + responseBody;
            SendErrorEmail("Erro ao criar timeline", message);
        }
    }
}

private static async Task<bool> AddUserToAccessGroup(string accessToken, string accessGroupId, string userId)
{
    // Obtém a string de conexão da base de dados
    string connectionString = ReadConnectionStringFromFile("connectionString.txt");

    // Busca os valores apiUrl, tokenUrl e clientId na base de dados
    var (tokenUrl, clientId, apiUrlBase, supportEmail, noReplyEmail, noReplyPassword, sendingPort, sendingServer) = await GetSysConectorSettingsFromDatabase(connectionString);

    if (string.IsNullOrEmpty(apiUrlBase))
    {
        Notas.Log("Erro: apiUrl não encontrado na base de dados.");
        return false; // Retorna falso se não conseguir obter o apiUrl
    }

    // Monta a URL da API para adicionar usuário ao grupo de acesso
    var apiUrl = $"{apiUrlBase}sites/{selectedSiteId}/access_groups/{accessGroupId}/users";
    var body = new JObject
    {
        { "add_ids", new JArray { userId } } // Adicionando o userId ao grupo
    };

    using (var httpClient = new HttpClient())
    {
        httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        var content = new StringContent(body.ToString(), System.Text.Encoding.UTF8, "application/json");

        var response = await httpClient.PatchAsync(apiUrl, content);
        var responseBody = await response.Content.ReadAsStringAsync();

        // Log da resposta para depuração
        Notas.Log("Resposta da API ao adicionar usuário ao grupo de acesso: ");
        Notas.Log(responseBody);

        return response.IsSuccessStatusCode; // Retorna verdadeiro se a operação foi bem-sucedida
    }
}

private static async Task<string?> GetLockId(string accessToken, string siteId, string protelRoomID)
{
    // Obtém a string de conexão da base de dados
    string connectionString = ReadConnectionStringFromFile("connectionString.txt");

    // Busca os valores apiUrl, tokenUrl e clientId na base de dados
    var (tokenUrl, clientId, apiUrlBase, supportEmail, noReplyEmail, noReplyPassword, sendingPort, sendingServer) = await GetSysConectorSettingsFromDatabase(connectionString);

    if (string.IsNullOrEmpty(apiUrlBase))
    {
        Notas.Log("Erro: apiUrl não encontrado na base de dados.");
        return null; // Retorna null se não conseguir obter o apiUrl
    }

    // Monta a URL da API para obter as fechaduras
    var apiUrl = $"{apiUrlBase}sites/{siteId}/locks";

    using (var httpClient = new HttpClient())
    {
        httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        var response = await httpClient.GetAsync(apiUrl);
        var responseBody = await response.Content.ReadAsStringAsync();

        // Log da resposta para depuração
        Notas.Log("Resposta da API ao obter fechaduras: ");
        Notas.Log(responseBody);

        if (response.IsSuccessStatusCode)
        {
            // Parse a resposta como JObject
            var jsonResponse = JObject.Parse(responseBody);
            var locks = jsonResponse["items"] as JArray; // Acesse o array de itens

            // Filtrar a fechadura que corresponde ao protelRoomID
            foreach (var lockObj in locks)
            {
                var customerReference = lockObj["customer_reference"]?.ToString();
                if (customerReference != null && customerReference.Contains(protelRoomID))
                {
                    return lockObj["id"]?.ToString(); // Retorna o ID da fechadura encontrada
                }
            }

            Notas.Log($"Nenhuma fechadura encontrada para o protelRoomID: {protelRoomID}");

            // Construa o corpo da mensagem
            string message = "Não foi encontrada nenhuma fechadura que contenha no nome o número do protelRoomID: " + protelRoomID;
            SendErrorEmail("Erro: nenhuma fechadura encontrada", message);

            return null; // Retorna null se não encontrar a fechadura
        }
        else
        {
            Notas.Log($"Erro ao obter fechaduras: {responseBody}");

            // Construa o corpo da mensagem
            string message = "Ocorreu um erro ao obter as fechaduras.\nResponse Body: " + responseBody;
            SendErrorEmail("Erro ao obter fechaduras", message);
            return null;
        }
    }
}


private static async Task<bool> AssociateLockToAccessGroup(string accessToken, string accessGroupId, string lockId)
{
    // Obtém a string de conexão da base de dados
    string connectionString = ReadConnectionStringFromFile("connectionString.txt");

    // Busca os valores apiUrl, tokenUrl e clientId na base de dados
    var (tokenUrl, clientId, apiUrlBase, supportEmail, noReplyEmail, noReplyPassword, sendingPort, sendingServer) = await GetSysConectorSettingsFromDatabase(connectionString);

    if (string.IsNullOrEmpty(apiUrlBase))
    {
        Notas.Log("Erro: apiUrl não encontrado na base de dados.");
        return false; // Retorna false se não conseguir obter o apiUrl
    }

    // Monta a URL da API para associar a fechadura ao grupo de acesso
    var apiUrl = $"{apiUrlBase}sites/{selectedSiteId}/access_groups/{accessGroupId}/locks";
    var body = new JObject
    {
        { "lock_id", lockId } // Adicionando o lockId ao grupo
    };

    using (var httpClient = new HttpClient())
    {
        httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        var content = new StringContent(body.ToString(), System.Text.Encoding.UTF8, "application/json");

        var response = await httpClient.PostAsync(apiUrl, content);
        var responseBody = await response.Content.ReadAsStringAsync();

        // Log da resposta para depuração
        Notas.Log("Resposta da API ao associar fechadura ao grupo de acesso: ");
        Notas.Log(responseBody);

        return response.IsSuccessStatusCode; // Retorna verdadeiro se a operação foi bem-sucedida
    }
}

 private static async Task<(string? pinCode, string responseBody, string requestUrl, int responseStatus, string requestType, string requestBody)> AssignPin(string accessToken, string siteId, string userId, DateTime expiryDate, string guestEmail, string guestName)
{
    // Obtém a string de conexão da base de dados
    string connectionString = ReadConnectionStringFromFile("connectionString.txt");

    // Busca os valores apiUrl, tokenUrl e clientId na base de dados
    var (tokenUrl, clientId, apiUrlBase, supportEmail, noReplyEmail, noReplyPassword, sendingPort, sendingServer) = await GetSysConectorSettingsFromDatabase(connectionString);

    if (string.IsNullOrEmpty(apiUrlBase))
    {
        Notas.Log("Erro: apiUrl não encontrado na base de dados.");
        return (null, "Erro: apiUrl não encontrado", "", 0, "PUT", ""); // Retorna com erro
    }

    // Monta a URL da API para atribuir o PIN
    var apiUrl = $"{apiUrlBase}sites/{selectedSiteId}/users/{userId}/pin";
    
    Thread.Sleep(2000); // Pausa de 2 segundos

    var body = new JObject
    {
        { "expiry_date", expiryDate.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") },
    };

    using (var httpClient = new HttpClient())
    {
        httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        var content = new StringContent(body.ToString(), System.Text.Encoding.UTF8, "application/json");

        var response = await httpClient.PutAsync(apiUrl, content); // Use PUT aqui
        var responseBody = await response.Content.ReadAsStringAsync();

        // Verificar se a resposta é uma string (o PIN) ou um objeto JSON
        if (response.IsSuccessStatusCode)
        {
            // Caso a resposta seja uma string simples (PIN)
            if (responseBody.StartsWith("\"") && responseBody.EndsWith("\""))
            {
                var pinCode = responseBody.Trim('"') + "#"; // Remover as aspas duplas
                Console.WriteLine($"PIN atribuído com sucesso! PIN: {pinCode}");
                await SendEmailWithPin(guestEmail, pinCode, guestName);

                return (pinCode, responseBody, apiUrl, (int)response.StatusCode, "PUT", body.ToString());
            }
            else
            {
                Console.WriteLine("Erro: A resposta não está no formato JSON esperado.");

                // Construa o corpo da mensagem
                string message = "A resposta não está no formato JSON esperado, impossibilitando a atribuição do pin.\nResponse Body: " + responseBody;
                SendErrorEmail("Erro: formato JSON inesperado", message);

                return (null, responseBody, apiUrl, (int)response.StatusCode, "PUT", body.ToString());
            }
        }
        else
        {
            Console.WriteLine($"Erro ao atribuir PIN: {responseBody}");

            // Construa o corpo da mensagem
            string message = "Não foi possivel atribuir um pin.\nResponse Body: " + responseBody;
            SendErrorEmail("Erro: Falha ao atribuir Pin", message);

            return (null, responseBody, apiUrl, (int)response.StatusCode, "PUT", body.ToString());
        }
    }
}

private static async Task UpdateRecordFields(SqlConnection connection, string recordId, string userId, string pinCode, string requestBody, string selectedSiteId, string responseBody, string requestUrl, int responseStatus, string requestType)
{
    try
    {
        // Verifica se o pinCode é nulo ou vazio e ajusta o valor do campo 'control'
        string controlValue = string.IsNullOrEmpty(pinCode) ? "N" : "S";

        var command = new SqlCommand(@"
            UPDATE requestRecordsCode SET 
                requestDate = @RequestDate,
                code = @Code, 
                siteID = @SiteID,
                saltoUserID = @SaltoUserID, 
                control = @Control, 
                requestBody = @RequestBody, 
                responseBody = @ResponseBody, 
                requestUrl = @RequestURL, 
                responseStatus = @ResponseStatus, 
                requestType = @RequestType 
            WHERE recordID = @recordID;
        ", connection);

        // Define os parâmetros
        command.Parameters.AddWithValue("@RequestDate", DateTime.Now);
        command.Parameters.AddWithValue("@Code", (object)pinCode ?? DBNull.Value);  // Permitir que o código seja nulo
        command.Parameters.AddWithValue("@SiteID", selectedSiteId);
        command.Parameters.AddWithValue("@SaltoUserID", userId);
        command.Parameters.AddWithValue("@Control", controlValue); // Define 'S' ou 'N' dependendo da existência do PIN
        command.Parameters.AddWithValue("@recordID", recordId);
        command.Parameters.AddWithValue("@RequestBody", (object)requestBody ?? DBNull.Value);
        command.Parameters.AddWithValue("@ResponseBody", responseBody);
        command.Parameters.AddWithValue("@RequestURL", (object)requestUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("@ResponseStatus", responseStatus);
        command.Parameters.AddWithValue("@RequestType", requestType);

        await command.ExecuteNonQueryAsync();

        Console.WriteLine($"Registro {recordId} atualizado com sucesso.");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Erro ao atualizar o registro com recordID {recordId}: {ex.Message}");

        // Construa o corpo da mensagem
            string message = "Erro ao atualizar o registo com o recordID: " + recordId + "\nMensagem: " + ex.Message;
            SendErrorEmail("Erro: Falha ao atualizar registo", message);

        // Se ocorrer um erro, loga na base de dados
        await LogErrorToDatabase(connection, recordId, selectedSiteId, userId, requestBody, responseBody, requestUrl, responseStatus, requestType);
    }
}

private static async Task LogErrorToDatabase(SqlConnection connection, string recordId, string siteId, string userId, string requestBody, string responseBody, string requestUrl, int responseStatus, string requestType)
{
    try
    {
        // Atualiza os detalhes do erro no banco de dados
        var updateCommand = new SqlCommand(@"
            UPDATE requestRecordsCode SET 
                requestDate = @RequestDate,
                siteID = @SiteID,
                saltoUserID = @SaltoUserID,
                requestBody = @RequestBody,
                responseBody = @ResponseBody,
                requestURL = @RequestURL,
                responseStatus = @ResponseStatus,
                requestType = @RequestType
            WHERE recordID = @RecordID;
        ", connection);

        // Adiciona os parâmetros para a query
        updateCommand.Parameters.AddWithValue("@RequestDate", DateTime.Now);
        updateCommand.Parameters.AddWithValue("@SiteID", (object)siteId ?? DBNull.Value);
        updateCommand.Parameters.AddWithValue("@SaltoUserID", (object)userId ?? DBNull.Value);
        updateCommand.Parameters.AddWithValue("@RequestBody", (object)requestBody ?? DBNull.Value);
        updateCommand.Parameters.AddWithValue("@ResponseBody", (object)responseBody ?? DBNull.Value);
        updateCommand.Parameters.AddWithValue("@RequestURL", (object)requestUrl ?? DBNull.Value);
        updateCommand.Parameters.AddWithValue("@ResponseStatus", responseStatus);
        updateCommand.Parameters.AddWithValue("@RequestType", requestType);
        updateCommand.Parameters.AddWithValue("@RecordID", recordId);

        // Executa o comando de atualização
        await updateCommand.ExecuteNonQueryAsync();

        Console.WriteLine($"Erro atualizado no banco de dados para o recordID {recordId}.");

        // Constrói a mensagem de erro para enviar por email (opcional)
        string message = $"Erro ao processar o registro com o recordID: {recordId}";
        SendErrorEmail("Erro: Falha ao atualizar registro", message);
    }
    catch (Exception ex)
    {
        // Trata erros durante a atualização no banco de dados
        Console.WriteLine($"Erro ao registrar erro na base de dados para o recordID {recordId}: {ex.Message}");

        // Constrói a mensagem de erro para enviar por email
        string message = $"Erro ao atualizar o registro com o recordID: {recordId}\nMensagem: {ex.Message}";
        SendErrorEmail("Erro: Falha ao atualizar registro", message);
    }
}


public static void Main(string[] args)
    {
        // Start the service properly
        ServiceBase.Run(new ServiceBase[] { new MyWindowsService() });
    }

}