@echo off
rem Cree la cle bot:read du compte liberastra-bot sur le VPS et ecrit les reglages
rem TrackerApi__* dans le .env du bot Liberastra sur panda, sans jamais afficher la cle :
rem elle passe directement du VPS a panda par un tube ssh.
rem Sert a la premiere mise en place et au renouvellement annuel (cle valable 364 jours).
rem
rem Prerequis : le compte liberastra-bot (non administrateur) existe sur le site, page Comptes ;
rem acces ssh par cle a serv_ovh et a panda (ssh de Windows, fichier .ssh\config).
rem
rem Usage : double-clic, ou depuis l'invite de commandes : deploy\create-bot-key.bat
rem Ensuite : redemarrer le bot pour qu'il lise les reglages.
setlocal
if not defined VPS set "VPS=serv_ovh"
if not defined BOT_HOST set "BOT_HOST=panda"
if not defined BOT_DIR set "BOT_DIR=/root/discord/Liberastra-Bot-Discord"
if not defined ACCOUNT set "ACCOUNT=liberastra-bot"
if not defined DAYS set "DAYS=364"
if not defined BASE_URL set "BASE_URL=https://141.95.51.193/bot-api/"

echo Verification du .env du bot sur %BOT_HOST%...
set "EXISTING="
for /f %%c in ('ssh %BOT_HOST% "grep -c ^TrackerApi__ %BOT_DIR%/.env"') do set "EXISTING=%%c"
if not defined EXISTING goto no_env
if "%EXISTING%"=="0" goto create
echo Des reglages TrackerApi existent deja dans le .env du bot.
choice /c ON /n /m "Les remplacer par une nouvelle cle, renouvellement ? [O/N] "
if errorlevel 2 goto unchanged

:create
echo Creation de la cle sur %VPS% et ecriture dans le .env de %BOT_HOST%...
ssh %VPS% "tr -d '\r' | bash -s -- %ACCOUNT% %DAYS% %BASE_URL%" < "%~dp0create-bot-key.remote.sh" | ssh %BOT_HOST% "umask 077; t=$(mktemp); cat > $t; if [ $(grep -c ^TrackerApi__ $t) != 3 ]; then rm -f $t; echo ECHEC : rien recu du VPS, .env inchange; exit 1; fi; cd %BOT_DIR% && cp .env .env.bak-tracker && sed -i '/^TrackerApi__/d' .env && sed -i -e '$a\' .env && cat $t >> .env && rm -f $t && echo OK : $(grep -c ^TrackerApi__ .env) reglages TrackerApi dans le .env du bot"
if errorlevel 1 goto failed
echo.
echo Termine. Il reste a redemarrer le bot pour qu'il lise les reglages.
goto end

:no_env
echo ECHEC : impossible de lire %BOT_DIR%/.env sur %BOT_HOST%.
goto failed

:unchanged
echo Rien n'a ete modifie.
goto end

:failed
echo.
echo Le .env du bot n'a pas ete modifie.

:end
endlocal
pause
