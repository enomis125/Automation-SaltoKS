using System;
using System.IO;

public static class Notas
{
    // Usando o caminho relativo para o arquivo de log
    private static readonly string logFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "notas.txt");

    public static void Log(string message)
    {
        try
        {
            // Adiciona uma nova linha no arquivo de log com a data e a hora
            using (var writer = new StreamWriter(logFilePath, true))
            {
                writer.WriteLine($"{DateTime.Now}: {message}");
            }
        }
        catch (Exception ex)
        {
            // Se houver um erro ao escrever no log, você pode querer tratá-lo aqui
            Console.WriteLine("Erro ao registrar no log: " + ex.Message);
        }
    }
}