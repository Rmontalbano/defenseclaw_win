# Run gateway as administrator

Administrator mode runs your installed DefenseClaw gateway with administrator rights. It is for one purpose: runtime monitoring's agent-actions plane (Plane C) reads the Windows Security log, and Windows only lets an elevated process read it. Everything else in the app works without it.

It is off by default, it needs DefenseClaw 1.0, and it is available only to local administrator accounts.

**Status:** this feature has not been run on a real machine yet. Its UAC prompts, the elevated gateway, and the file ownership it leaves behind were tested with fakes and temporary folders only.

## Turn it on

Switch it on under Overview > Quick Actions, or in Settings > Connection. The switch starts and stops nothing by itself. The app shows a status line, and two buttons when they apply: **Prepare administrator copy** and **Repair ownership**.

The switch is disabled, with the reason shown, when:

- the runtime is DefenseClaw 0.8.10 (no runtime monitoring);
- the account is a standard user. Windows would then ask for another account's password, and the gateway would run as that account;
- the account is not a split-token administrator or already elevated.

Turning the switch off is always allowed.

## What it does

With the switch on, the gateway buttons read **Start / Restart / Stop Gateway as Administrator**. Each one:

1. builds a plan and shows a review of exactly what will run elevated;
2. asks Windows for approval through a UAC prompt, once per step.

A first start with no copy installed is two steps (install the copy, then start), so two prompts. Stop first runs the ordinary graceful `defenseclaw-gateway stop` with its normal review. Only if the gateway is still running does it ask for an elevated stop.

### The administrator copy

The elevated process does not run the gateway from your profile. The helper copies your installed gateway to a folder only administrators can write:

- `%ProgramFiles%\DefenseClaw Companion\AdminGateway\<your SID>\defenseclaw-gateway.exe`, with a `copy.json` (hash, version, source hash, signature state) and `last-result.json` beside it.
- Every file there has an explicit protected ACL: SYSTEM and Administrators have full control, Users can read and run, and the owner is Administrators.
- The copy is made only when the installed gateway at `~\.local\bin\defenseclaw-gateway.exe` has exactly the same hash. A source build or a modified file is refused.

Only Restart refreshes the copy. A Start with a copy that differs from the installed gateway is refused.

The app sends the gateway token to the elevated gateway only once it has recognised it as this copy: the process runs your copy's exact path, the folders are admin-only, the file equals `copy.json`, and the process runs in the app's own Windows session (Users may run the copy, so the path alone does not say it is yours). When Windows will not let the unelevated app open the elevated process, the app reads its path from the kernel's process list instead. The dashboard then says "The administrator gateway started by this app is responding". A gateway whose path cannot be read, or that runs in another session, is called unverified and gets no token; the helper's `last-result.json` and the gateway's `gateway.pid` never stand in for the path.

The process must also be the one that opened the socket it answers on. Windows keeps naming a socket by its creator's process id after the creator exits, and a later process can reuse that id, so the app compares the socket's creation time with the process's: a process younger than its socket by more than one clock tick (16 ms, in case one of the two times is rounded), or one whose times Windows does not give, is refused. A connection is also held to the process's listening sockets on the port in both the IPv4 and the IPv6 table, and refused when either table cannot be read. The Runtime monitoring card and the status line call the gateway "running with administrator rights" only for the copy running in the app's own session; the copy run from another session is not called the helper's gateway.

Not closed, and accepted: a Windows session is not an account. A process that another account starts inside your session (with `runas`, which needs that account's password) and that runs the copy is judged by its path and session alone, because Windows shows a process's owner only to a program that can open it, and an elevated or foreign process cannot be opened by the unelevated app.

To verify on a real machine (not yet done): the established connection rows of the elevated gateway carry a creation time; both socket tables (IPv4 and IPv6) can be read by the unelevated app, also with IPv6 turned off; and the gateway opens its own listening socket, so a socket inherited from a launcher that has exited is still refused.

### The helper

`DefenseClaw.GatewayAdmin.exe` is a console program published beside the app and listed in `checksums.txt`. It takes exactly one argument: `install-copy`, `start`, `restart`, `stop`, `repair-ownership` or `status`. Anything else exits with code 64 before it does anything. It never takes paths as arguments. Paths come from the Program Files folder, the token's SID and your profile folder.

Before it acts, the helper checks that:

- the token is elevated and is the full half of a split token, and the parent process runs as the same account;
- no `DOTNET_STARTUP_HOOKS`, `CORECLR_*`, `COR_*` or `COMPlus_*` variables are set;
- it runs from the admin-only folder, and that folder, its parents and the file are owned by Administrators, SYSTEM or TrustedInstaller, with no other principal able to write them and no reparse points.

The `status` verb needs no elevation.

The app starts the helper only through the Windows `runas` verb. The app has no other path that can raise a prompt.

## The trust limit

Read this before you turn it on.

- **The elevated gateway is the gateway code, running as administrator, for as long as it runs.** A bug or a compromise in that code runs with administrator rights. The helper limits what it will run. It does not limit what the gateway does once started.
- **Anyone who can change the admin folder can run code as administrator.** The folder is admin-only for that reason. The app does not weaken those rights and checks them before each run.
- **The copy is checked, not signed.** The helper matches hashes. The executables are not Authenticode-signed. Your trust rests on the release checks and on your own copy of DefenseClaw being the one you meant to install.
- **Your user owns the files the gateway creates while it runs as you.** An elevated token would otherwise make its files Administrators-owned, and the 1.x gateway then refuses to read them (see below).

If you are not comfortable with that, leave the switch off. Runtime monitoring still shows what it can, and the Runtime panel says which checks it cannot make without elevation.

## Ownership and repair

An elevated gateway creates files as Administrators. The app checks the owner of `gateway.pid`, `hook_contract_lock.json`, `audit.db`, `.env`, `device.key`, `gateway.log` and `config.yaml`. When any is owned by Administrators:

- the status shows it;
- a Start or Restart (either path) is refused, with the reason: "This installation has files owned by Administrators because the gateway ran as administrator. Run Repair ownership, or keep running the gateway as administrator."

**Repair ownership** runs a reviewed `repair-ownership` step. It walks only `%USERPROFILE%\.defenseclaw`. It does not follow or change reparse points. It changes the owner only of objects that are owned by Administrators, leaves objects with other owners alone, and reports how many it left. It needs the ownership and restore privileges, and it asks for approval first.

## Turn it off, and remove the copy

1. Stop the gateway with **Stop Gateway as Administrator**. The ordinary stop may not be able to stop an elevated gateway.
2. Switch administrator mode off.
3. To remove the copy, delete `%ProgramFiles%\DefenseClaw Companion` from an elevated prompt. The app does not delete it, and the uninstaller does not remove it.
4. If the files under `%USERPROFILE%\.defenseclaw` are now owned by Administrators, run **Repair ownership** before you start the gateway as yourself.

## Not verified yet

Before this is trusted on a real machine, the following should be checked by hand:

1. Prepare administrator copy: the UAC prompt appears and names the helper; the helper's window shows the hashes; afterwards the copy exists and its files have the expected owners and rights.
2. That a file opened with read-only sharing can still be started by the elevated process.
3. That the elevated process's parent is the app, and whether it inherits the app's environment.
4. Start as administrator: the gateway keeps running after the helper exits, the app treats it as trusted, and the files it creates belong to your user.
5. Stop: that the ordinary stop cannot stop an elevated gateway, and that the elevated stop does; the format of `gateway.pid`.
6. Repair ownership, after running the gateway elevated by hand.
7. That the release zip contains the helper under the expected name, and the cosign path.
8. That a standard-user account is refused.
