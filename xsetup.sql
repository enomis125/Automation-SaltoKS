USE protelmprado;

DECLARE @Ref int, @Ref2 int, @Ref3 int, @Ref4 int, @Ref5 int, @Ref6 int, @Ref7 int, @Ref8 int, @Ref9 int, @Ref10 int, @Ref11 int, @Mpehotel int
DECLARE @tokenURL nvarchar(max), @clientId nvarchar(max), @apiUrl nvarchar(max)
DECLARE @timeToAlive datetime, @noReplyEmail nvarchar(255), @noReplyPassword nvarchar(255)
DECLARE @sendingServer nvarchar(255), @sendingPort int
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
SET @sendingServer = 'smtp.gmail.com'
SET @sendingPort = 587
SET @supportEmail = 'ssantos.micronet@gmail.com'

IF NOT EXISTS (SELECT * FROM proteluser.xsetup WHERE xsection = 'SysConector')
BEGIN
    SET @Ref = (SELECT MAX(ref) + 1 FROM proteluser.xsetup)
    SET @Ref2 = @Ref + 1
    SET @Ref3 = @Ref2 + 1
    SET @Ref4 = @Ref3 + 1
    SET @Ref5 = @Ref4 + 1
    SET @Ref6 = @Ref5 + 1
    SET @Ref7 = @Ref6 + 1
    SET @Ref8 = @Ref7 + 1
    SET @Ref9 = @Ref8 + 1
    SET @Ref10 = @Ref9 + 1
    SET @Ref11 = @Ref10 + 1

    INSERT INTO proteluser.xsetup
    VALUES 
        (@Ref, @Mpehotel, 'SysConector', 'tokenURL', @tokenURL),
        (@Ref2, @Mpehotel, 'SysConector', 'clientId', @clientId),
        (@Ref3, @Mpehotel, 'SysConector', 'apiUrl', @apiUrl),
        (@Ref4, @Mpehotel, 'SysConector', 'timeToAlive', CAST(@timeToAlive AS NVARCHAR(MAX))),
        (@Ref5, @Mpehotel, 'SysConector', 'noReplyEmail', @noReplyEmail),
        (@Ref6, @Mpehotel, 'SysConector', 'noReplyPassword', @noReplyPassword),
        (@Ref7, @Mpehotel, 'SysConector', 'sendingServer', @sendingServer),
        (@Ref8, @Mpehotel, 'SysConector', 'sendingPort', CAST(@sendingPort AS NVARCHAR(MAX))),
        (@Ref9, @Mpehotel, 'SysConector', 'supportEmail', @supportEmail)
END;
