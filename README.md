# Life Dashboard - Backend (C# / .NET 8)

Minimal-API web service on top of MS SQL Server. JWT auth, EF Core, file uploads
for documents, and a deadline engine that turns records into the alert feed.

## Prerequisites

- .NET 8 SDK
- An MS SQL Server database (create the schema with the scripts in `../database`)

## Run locally

    cd LifeDash.Api
    dotnet restore
    dotnet run

Swagger UI opens at http://localhost:5080/swagger.

## Configure

Edit `LifeDash.Api/appsettings.json`, or override with environment variables
(double underscore separates levels):

| Setting | Meaning |
|---|---|
| `ConnectionStrings__Default` | SQL Server connection string |
| `Jwt__Key` | Signing key, at least 32 characters. The app refuses to start without it. |
| `Jwt__ExpiryHours` | Token lifetime, default 72 |
| `Auth__AllowRegistration` | Set to `false` once your accounts exist |
| `Cors__AllowedOrigins__0` | Your frontend origin, e.g. `https://lifedash.pages.dev` |
| `Storage__UploadPath` | Where uploaded documents are written |
| `Storage__MaxUploadMb` | Upload size limit, default 20 |
| `Swagger__Enabled` | Set to `false` in production |
| `ReminderTrigger__ApiKey` | Secret required by the external reminder trigger. Use a random value of at least 32 characters. |

## API surface

| Method | Route | Notes |
|---|---|---|
| POST | `/api/auth/register` | disabled when `Auth:AllowRegistration` is false |
| POST | `/api/auth/login` | returns token, userId, email, displayName |
| GET | `/api/auth/me` | current session |
| GET | `/api/dashboard?horizonDays=120` | summary, alerts, insights |
| CRUD | `/api/family-members`, `/api/appointments`, `/api/important-dates` | |
| CRUD | `/api/incomes`, `/api/fixed-costs`, `/api/subscriptions`, `/api/payments` | |
| CRUD | `/api/home-items`, `/api/tasks`, `/api/documents` | |
| CRUD | `/api/authority-cases` | includes the nested required-document checklist |
| POST | `/api/authority-cases/{caseId}/required-documents/{reqId}/link/{documentId}` | tick off a checklist row |
| CRUD | `/api/trips` | plus `/bookings` and `/packing` sub-resources |
| POST | `/api/documents/{id}/file` | multipart upload |
| GET | `/api/documents/{id}/file` | download |
| GET | `/api/health` | checks the database connection |

Every route except `/`, `/api/health` and `/api/auth/*` needs
`Authorization: Bearer <token>`. Records are always filtered by the user id in
the token, so one account never sees another's data.

## The deadline engine

`Services/DeadlineEngine.cs` is the heart of the app. It reads ten sources -
document expiry, authority deadlines and next actions, missing mandatory
documents, unpaid payments, subscription cancellation windows, warranties,
appointments, yearly dates, open tasks, and the next trip - and grades each one:

| Days left | Severity |
|---|---|
| below 0 | Overdue |
| 0 to 7 | Urgent |
| 8 to max(reminderDays, 14) | Soon |
| beyond that | Info |

Cancellation deadlines take priority over renewal dates, because the cancellation
window is the date you can still act on. Yearly dates roll forward to their next
occurrence, with a guard for 29 February.

Insights are separate: subscriptions renewing next calendar month, missing
mandatory documents, the monthly budget balance, and documents expiring within
90 days.

## Publish to ASPnix (asp-monster)

    cd LifeDash.Api
    dotnet publish -c Release -o ./publish

Upload the contents of `publish/` to your site root over FTP or the file manager.
`web.config` is included and already points at `LifeDash.Api.dll` with in-process
hosting.

Then, in the ASPnix control panel:

1. Set the application pool to No Managed Code (.NET Core does not use the CLR).
2. Make sure the ASP.NET Core Hosting Bundle 8.x is available on the plan. If it
   is not, publish self-contained instead:
   `dotnet publish -c Release -r win-x86 --self-contained true -o ./publish`
3. Set the real connection string and `Jwt__Key` - either edit `appsettings.json`
   before uploading, or add them as environment variables in the panel.
4. Give the app write permission on the `App_Data` folder so uploads work.
5. Add your Cloudflare Pages or GitHub Pages origin to `Cors:AllowedOrigins`.
6. Set `Swagger__Enabled` to `false` and `Auth__AllowRegistration` to `false`
   once your account exists.

Confirm the deployment with `https://your-domain/api/health`.

## Keep reminder emails running on ASPnix

ASPnix can stop an idle application, so use an external scheduler to call the
reminder endpoint every five minutes. The endpoint runs all email reminder
checks: appointments, flights, birthdays, tasks, payments, fixed costs, income,
contract cancellation dates, and authority deadlines. Repeated calls do not
resend a reminder that was already recorded as sent.

1. Generate a random secret with `openssl rand -hex 32` (or another password
   generator), then set it in the ASPnix environment as
   `ReminderTrigger__ApiKey`. Do not put the real value in source control.
2. In cron-job.org, create a new cron job with these settings:

   | Field | Value |
   |---|---|
   | URL | `https://your-api-domain/api/jobs/reminders` |
   | Request method | `POST` |
   | Schedule | Every 5 minutes (`*/5 * * * *`) |
   | Request header | `X-Reminder-Trigger: <the same random secret>` |
   | Request body | Empty |

3. Save the job, use **Run now** once, then inspect its execution history. A
   successful call returns `{"status":"completed"}`. During an overlapping
   request it returns `{"status":"already-running"}`; this is also a healthy
   result. A `401` means the header value does not match, and a `503` means the
   API key has not been configured on the server yet.

The route only accepts `POST` and requires the secret header. Do not use the
regular app JWT for this job.

## Keep the Postfach scan running automatically

Alongside the manual "Postfach scannen" buttons on the Deliveries and Family
pages, a second cron job scans the same mailbox for Sendungen (packages) and
Termine (appointments) on a schedule and writes into the same account. It
reuses the `ReminderTrigger:ApiKey` secret set up above, under a different
header, so no new secret needs to be generated.

1. Set `MailTracking__OwnerUserId` to your account's numeric user id. Find it
   by calling `GET /api/auth/me` while signed in (e.g. from the browser
   console: `fetch("/api/auth/me", { headers: { Authorization: "Bearer " + token }}).then(r => r.json())`)
   — the response's `userId` field is the value to use.
2. In cron-job.org, create a new cron job with these settings:

   | Field | Value |
   |---|---|
   | URL | `https://your-api-domain/api/jobs/mail-scan` |
   | Request method | `POST` |
   | Schedule | Every 6 hours (`0 */6 * * *`) |
   | Request header | `X-MailScan-Trigger: <same secret as ReminderTrigger:ApiKey>` |
   | Request body | Empty |

3. Save the job, use **Run now** once, then inspect its execution history. A
   successful call returns `{"status":"completed","packages":{...},"appointments":{...}}`.
   A `401` means the header value does not match, and a `503` means either the
   API key or `MailTracking:OwnerUserId` has not been configured yet.

Termine detected this way are added with a `[mail-scan]` note so they're easy
to tell apart from manually entered ones, and are never added twice for the
same date, time and person.

## Security notes

- Passwords use PBKDF2-SHA256 with 120,000 iterations and a per-user salt.
- Uploaded files are stored outside the web root under `Storage:UploadPath` and
  are only served through the authenticated download endpoint.
- Change `Jwt:Key` before going live. Rotating it signs everyone out.
