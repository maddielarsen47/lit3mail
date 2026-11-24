# Lit3Mail

### Free for commercial and personal use. Currently in BETA.

Lit3Mail is a **simple** Windows desktop email client AI-coded as a hobby project. 
It was designed and tested with Gmail and can also connect to
other IMAP and SMTP servers that accept passwords.

- **Password login only.** Lit3Mail supports passwords and provider-issued app
  passwords. It does not support OAuth, so accounts such as Outlook.com cannot connect.
- **No conversation view.** Messages appear individually in each folder, by design.
- **Explicit read status.** Messages must be ticked as read explicitly, by design.
- **No calendar/non-email integration.** Excluded to keep scope small, by design.

### Licence &amp; Disclaimer

Lit3Mail's original code and documentation
are dedicated to the public domain under [CC0 1.0 Universal](LICENCE.md).
The software is provided as-is, without warranty.

## Using Lit3Mail

### Install and Connect

1. Ensure `.NET 10 Desktop Runtime` and `Microsoft Edge WebView2 Evergreen Runtime` are installed.
2. Download the portable ZIP release, extract to the location of your choice (e.g. C:/Program Files/Lit3Mail).
3. Create shortcuts as desired to Lit3Mail.exe, and run it.
4. Configure your account details in **Settings**.

For Gmail, enable **2-Step Verification** in your Google Account and generate
an [App Password](https://support.google.com/accounts/answer/185833) for
Lit3Mail. Use that App Password in both password fields. New accounts default to
`imap.gmail.com` on port 993 and `smtp.gmail.com` on port 465 with implicit TLS.

### Updates
- For automatic updates, use the Check for Updates... button in **Settings** > General.
- For manual updates, delete all files in the program folder, then extract the ZIP to replace.

### Read and manage mail

- Lit3Mail refreshes inboxes once per minute, and performs full refresh 
  every 10 minutes, while open.
- When Windows allows banners, new mail notifications stay visible until you
  dismiss them or return to Lit3Mail. Turn them off per account under
  **Desktop notifications** in Settings.
- **Search** filters the current folder in view only.
- Gmail labels exposed over IMAP appear after the standard folders, marked
  with an asterisk. Lit3Mail skips **All Mail**, **Important**, and **Starred**.
  To make another label available, enable **Show in IMAP** for it
  in Gmail's Labels settings. [Show or hide Gmail labels over IMAP](https://support.microsoft.com/en-us/outlook/how-to-show-or-hide-folders-for-imap-in-google-settings).
- Use **Ctrl** to select individual messages or **Shift** to select a range.
  Drag the selection onto another folder in the same account to move it.
- **Del** asks for confirmation before moving messages to Trash or permanently
  deleting them from Trash. Once confirmed, messages disappear from the list
  immediately and return if the server operation fails.
- Use **Open EML...** in the sidebar or **Ctrl+O** to view a saved email in the
  preview panel. You can also use Windows **Open with** to pass an EML file to Lit3Mail. 
  Opening a file does not import it into a mailbox.
- Use **Export** in the sidebar to save selected messages or the entire
  current folder as a ZIP of EML files. Folder export includes every message
  in the folder, even when a search filter is active. You can cancel an
  export while it runs.
- Drag account tabs in **Settings** to change their order in the mail sidebar.
- Untick **Images & Remote Content** in the folders panel to hide images by default
  and prevent senders from capturing mail-open data.
- Lit3Mail’s interface is English-only, but the composer and message preview support Unicode, including
  Chinese, Japanese, Korean, Cyrillic, and accented Latin scripts. Emoji also work where the font supports them.
  Arabic and Hebrew text can be entered, but the composer currently has no dedicated right-to-left paragraph control.
- **Windows Credential Manager** stores mail-server passwords for the current
  Windows user. Use **Forget saved sign-in** in Settings to remove them.
- The app's local database location is configurable in **Settings**. Default: 
  `%LOCALAPPDATA%\Lit3Mail\lit3mail.db`
  
### Offline drafts

If the mail server is unavailable, **Save Draft** saves the message and its
attachments locally. Open or delete these drafts under **Local Drafts**
in the sidebar. They survive an app restart. When you're back online, open the
draft and choose **Save Draft** to save it to the server or **Send Mail** to send
it. Lit3Mail does not queue messages for sending.

### Limits

The composer allows up to **25 MiB per file** and **50 MiB total** in file
attachments. If a selected file exceeds either limit, Lit3Mail skips it and
lists it in the composer status. If a server draft exceeds either attachment limit or
its full message exceeds **80 MiB**, Lit3Mail shows an error and leaves the
draft on the server.

Up to 1,000 recent emails are cached per folder for quick access in the local data file.

## Technical details

### Build

Built in C#, WinForms, for Windows, using the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).
The [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)
provides HTML previews. Lit3Mail can display the plain-text version if the
runtime is unavailable.

### Architecture and storage

- **WinForms** provides the desktop interface. 
- **MailKit** and **MimeKit** handle TLS, IMAP and SMTP 
  connections, message parsing, and delivery.
- **WebView2** renders received HTML. 
- **RtfPipe** converts rich-text drafts to
  HTML for sending with a plain-text alternative.
- **SQLite** stores app data including account settings, cached messages, 
  known addresses, and local drafts.
- **Windows Credential Manager** stores mail-server passwords for the current 
  Windows user.
- **Other** Lit3Mail stores window layout and WebView2 profile data in:
  `%LOCALAPPDATA%\Lit3Mail`.
