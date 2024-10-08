USE [protelmprado] 

DECLARE @Mpehotel int, @tokenURL nvarchar(max), @clientId nvarchar(max), @apiUrl nvarchar(max)
DECLARE @timeToAlive datetime, @noReplyEmail nvarchar(255), @noReplyPassword nvarchar(255)
DECLARE @sendingServer nvarchar(255), @sendingPort int, @user nvarchar(255), @password nvarchar(255)
DECLARE @supportEmail nvarchar(255)

-- Definição dos valores padrão
SET @tokenURL = 'https://clp-accept-identityserver.saltoks.com/connect/token'
SET @clientId = '956ebbbe-785a-4948-8592-ad2b826b0e6a'
SET @apiUrl = 'https://clp-accept-user.my-clay.com/v1.1/'
SET @Mpehotel = 1 

-- Definir os novos valores padrão
SET @timeToAlive = GETDATE() 
SET @noReplyEmail = 'pmonteiro.micronet@gmail.com'
SET @noReplyPassword = 'qaev zpjt rnpt nnao'
SET @sendingServer = 'smtp.example.com'
SET @sendingPort = 587 
SET @user = 'emailuser@example.com'
SET @password = 'emailpassword'
SET @supportEmail = 'ssantos.micronet@gmail.com' 

-- Verifica se a tabela dbo.xsetup existe e, se não, cria a tabela
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[xsetup]') AND type in (N'U'))
BEGIN
    -- Cria a tabela dbo.xsetup se ela não existir
    CREATE TABLE [dbo].[xsetup] (
        Ref INT PRIMARY KEY IDENTITY(1,1), 
        Mpehotel INT NOT NULL,
        xsection NVARCHAR(255) NOT NULL,
        xkey NVARCHAR(255) NOT NULL,
        xvalue NVARCHAR(MAX) NOT NULL
    )
END

-- Verifica se já existe um registro para o 'SysConector'
IF NOT EXISTS (SELECT 1 FROM [dbo].[xsetup] WHERE xsection = 'SysConector')
BEGIN
    -- Insere os dados na tabela
    INSERT INTO [dbo].[xsetup] (Mpehotel, xsection, xkey, xvalue)
    VALUES 
        (@Mpehotel, 'SysConector', 'tokenURL', @tokenURL),
        (@Mpehotel, 'SysConector', 'clientId', @clientId),
        (@Mpehotel, 'SysConector', 'apiUrl', @apiUrl),
        (@Mpehotel, 'SysConector', 'timeToAlive', CAST(@timeToAlive AS NVARCHAR(MAX))),
        (@Mpehotel, 'SysConector', 'noReplyEmail', @noReplyEmail),
        (@Mpehotel, 'SysConector', 'noReplyPassword', @noReplyPassword),
        (@Mpehotel, 'SysConector', 'sendingServer', @sendingServer),
        (@Mpehotel, 'SysConector', 'sendingPort', CAST(@sendingPort AS NVARCHAR(MAX))),
        (@Mpehotel, 'SysConector', 'user', @user),
        (@Mpehotel, 'SysConector', 'password', @password),
        (@Mpehotel, 'SysConector', 'supportEmail', @supportEmail)
END
