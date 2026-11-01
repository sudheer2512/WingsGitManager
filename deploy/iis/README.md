# Windows Server + IIS deployment

1. Install the **.NET 10 Hosting Bundle**, **Git for Windows** (make sure `git.exe` is on the machine PATH, or set `GitManager:GitExecutable`), and enable the IIS **WebSocket Protocol** feature (SignalR).
2. Publish: `dotnet publish src/Wings.GitManager.Api -c Release -o C:\inetpub\wings-git-manager` and copy the built UI (`web` → `npm ci && npm run build`) — it lands in `src/Wings.GitManager.Api/wwwroot` and is included by publish. Copy `deploy/iis/web.config` over the generated one.
3. Create an application pool **"WingsGitManager"** (No Managed Code, Integrated). Run it as a dedicated low-privilege account (e.g. `WINGS\svc-gitmanager`) that has Modify rights only on the workspace root, the data directory and the local backup staging folder.
4. Configuration: put secrets in environment variables of the app pool identity or in Azure Key Vault / DPAPI-protected `appsettings.Production.json` outside the web root:
   `ConnectionStrings__GitManager`, `Database__Provider=SqlServer`, `Database__AutoCreate=false`, `Gitea__BaseUrl`, `Gitea__Token`, `Gitea__ServiceUser`, `Backup__EncryptionKey`, `GitManager__WorkspaceRoots__0` (e.g. `D:\WingsWorkspaces`), `GitManager__DataDirectory` (e.g. `D:\WingsGitManager\data`).
5. Apply `database/sqlserver/create.sql` to an empty SQL Server database once.
6. Worker as a Windows Service (same configuration, same service account):
   ```powershell
   dotnet publish src/Wings.GitManager.Worker -c Release -o C:\Services\WingsGitManagerWorker
   sc.exe create WingsGitManagerWorker binPath= "C:\Services\WingsGitManagerWorker\Wings.GitManager.Worker.exe" start= delayed-auto obj= "WINGS\svc-gitmanager"
   sc.exe failure WingsGitManagerWorker reset= 86400 actions= restart/5000/restart/5000/restart/60000
   sc.exe start WingsGitManagerWorker
   ```
7. Bind HTTPS in IIS with a trusted certificate; keep `Https:Redirect=true`.
8. Read the bootstrap administrator token from `<DataDirectory>\bootstrap-admin-token.txt`, store it in a password manager and delete the file.

Not verified on this development Mac: IIS hosting and the Windows Service were written from the standard ASP.NET Core patterns but have not been run on Windows yet.
