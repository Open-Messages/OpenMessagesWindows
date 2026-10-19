# OpenMessages for Windows

A WPF app that shows the verification codes synced from [OpenMessages](https://github.com/Open-Messages/OpenMessages) on your Android phone. It is the Windows counterpart of [OpenMessages for Mac](https://github.com/Open-Messages/OpenMessagesMac). Codes are encrypted on the phone and decrypted only on this PC, using a sync key that only you know.

- The main window lists recent codes, with **Copy** and **Delete** for each one.
- An icon in the notification area keeps running after you close the window. Right-click it to see the latest five codes and click one to copy it.
- Optional notification and auto-copy when a new code arrives. Copied codes are kept out of Windows clipboard history and cloud clipboard.
- **Start with Windows** is optional. The app then starts minimized to the notification area.
- New codes appear within about 5 seconds. Expired codes (older than 24 hours) are hidden.

## Layout

```
src/OpenMessages.Core/       net10.0: crypto, config, Google OAuth, Firebase Auth/Firestore REST, OtpSyncService
src/OpenMessages.Windows/    net10.0-windows: WPF UI, tray icon, DPAPI storage, settings
tests/OpenMessages.Core.Tests/  xUnit v3 tests, including the crypto test vector shared with Android and macOS
```

`OpenMessages.Core` has no Windows dependencies, so you can build and test it on macOS or Linux. The WPF project also builds there, because it sets `EnableWindowsTargeting`, but it only runs on Windows.

## How it works

The Windows app follows the same protocol as the Mac app. It talks to Firebase directly over REST and does not use a Firebase SDK.

1. **Google sign-in.** It opens Google in your default browser and uses the authorization code flow with PKCE. Google redirects to a one-time listener on `http://127.0.0.1:<random port>/`. This works for any Windows user and needs no admin rights or URL reservation.
2. **Firebase Auth.** It exchanges the Google ID token through `accounts:signInWithIdp`, then refreshes tokens through `securetoken.googleapis.com`.
3. **Firestore.** It reads `users/{uid}.otpSync` to get the key parameters. It then polls `users/{uid}/otps` with a `createdAt >=` cursor, so each poll reads only new records.
4. **Decryption.** It derives the key with PBKDF2-HMAC-SHA256 (`Rfc2898DeriveBytes.Pbkdf2`) and opens each record with AES-256-GCM (`AesGcm`). The format is specified in [`OpenMessages/docs/otp-sync.md`](https://github.com/Open-Messages/OpenMessages/blob/main/docs/otp-sync.md).

The Firebase session and the derived sync key are encrypted with DPAPI for your Windows account and stored in `%APPDATA%\OpenMessages\`. The passphrase itself is not stored.

## Setup

1. Follow the Firebase setup in [`OpenMessages/docs/otp-sync.md`](https://github.com/Open-Messages/OpenMessages/blob/main/docs/otp-sync.md). Your Firebase project needs Google sign-in enabled, Firestore created and the rules deployed.
2. **Create a Desktop OAuth client.** In Google Cloud Console, open the same project, then go to **APIs & Services › Credentials › Create credentials › OAuth client ID** and choose application type **Desktop app**. Copy its client ID and client secret. Google documents that a desktop client secret is not confidential, so it is fine to ship it in the config file.
3. **Get the Firebase values.** In Firebase, open **Project settings › General** and copy the **Web API key** and the **Project ID**.
4. Copy `firebase-config.example.json` to `firebase-config.json` and fill in the four values:

   ```json
   {
     "apiKey": "AIza…",
     "projectId": "your-project",
     "googleClientId": "…apps.googleusercontent.com",
     "googleClientSecret": "GOCSPX-…"
   }
   ```

   The app looks for this file next to `OpenMessages.exe` first, then in `%APPDATA%\OpenMessages\`.
5. Build on Windows, which needs the .NET 10 SDK:

   ```powershell
   scripts\publish.ps1 -Config firebase-config.json   # → publish\win-x64\OpenMessages.exe (self-contained)
   ```

   For development, use `dotnet run --project src/OpenMessages.Windows` instead.
6. On the phone, open Messages › Settings › OTP sync. Sign in, create a sync key and turn on **Sync verification codes**.
7. On the PC, sign in with the same Google account and enter the sync key.

If Firebase rejects the sign-in with an error like `INVALID_IDP_RESPONSE` or an audience mismatch, go to **Firebase › Authentication › Sign-in method › Google** and add the Desktop client ID under **Safelist client IDs from external projects**.

## Development

```sh
dotnet build                                          # all projects; works on macOS/Linux too
dotnet test --project tests/OpenMessages.Core.Tests   # Microsoft.Testing.Platform (see global.json)
```

## Notes

- **Firestore reads.** Each poll costs about one read, even when nothing changes. At a 5-second interval that is about 17,000 reads per day while the app runs. Change `OtpSyncService.PollInterval` if you need fewer.
- **Changing the key.** If you change the sync key on the phone, the PC notices the new salt within about 5 minutes, or straight away when you click Refresh or press F5, and asks for the new key.
- **SmartScreen warning.** The executable is unsigned, so Windows SmartScreen will warn the first time it runs. Sign it with a code-signing certificate before you distribute it.
