# OfrinioAngularAzureCalendar

This project was generated using [Angular CLI](https://github.com/angular/angular-cli) version 21.2.14.

## Development server

To start a local development server, run:

```bash
ng serve
```

Once the server is running, open your browser and navigate to `http://localhost:4200/`. The application will automatically reload whenever you modify any of the source files.

## Code scaffolding

Angular CLI includes powerful code scaffolding tools. To generate a new component, run:

```bash
ng generate component component-name
```

For a complete list of available schematics (such as `components`, `directives`, or `pipes`), run:

```bash
ng generate --help
```

## Building

To build the project run:

```bash
ng build
```

This will compile your project and store the build artifacts in the `dist/` directory. By default, the production build optimizes your application for performance and speed.

## Running unit tests

To execute unit tests with the [Vitest](https://vitest.dev/) test runner, use the following command:

```bash
ng test
```

## Running end-to-end tests

For end-to-end (e2e) testing, run:

```bash
ng e2e
```

Angular CLI does not come with an end-to-end testing framework by default. You can choose one that suits your needs.

## Azure SQL & API deployment

This project includes a small .NET 8 API at `api/Ofrinio.Api` and an Azure Functions version at `api/Ofrinio.Functions`. The API reads public availability from Azure SQL, stores booking requests, and exposes protected owner endpoints for calendar edits.

- The public website is hosted on GitHub Pages at `https://martin9020.github.io/ofrinio-angular-azure-calendar/`.
- Azure stays underneath the site as the API and SQL backend: `https://ofrinio-api-martin9020.azurewebsites.net`.
- Configure the Azure SQL connection string with the `ConnectionStrings:OfrinioSql` setting in `api/Ofrinio.Api/appsettings.Development.json`, or set the `AZURE_SQL_CONNECTION_STRING` environment variable in production.
- Configure `OFRINIO_ADMIN_TOKEN_SECRET` with a long random value for signing 12-hour admin sessions.
- Owner accounts live in `dbo.AdminUsers`. Seed or update them through `POST /api/owner/bootstrap-users` using a temporary `OFRINIO_ADMIN_BOOTSTRAP_TOKEN`, then remove that bootstrap setting.
- The frontend reads the API base URL from `window.OFRINIO_API_BASE` in `src/index.html`.
- The public frontend reads availability only from Azure. If Azure is unavailable, it shows the built-in demo calendar and does not read Supabase directly.
- The owner login is available at `#/login`. After login it redirects to `#/owner`, a calendar-only editor inspired by `martin9020/calendar`. It writes booked/pending/free date ranges into Azure SQL, can import old Supabase availability into Azure SQL, and can turn scheduled Supabase sync on/off.
- Supabase import and sync use `SUPABASE_URL` and either `SUPABASE_SERVICE_ROLE_KEY` or `SUPABASE_ANON_KEY`. With the service-role key, it can import reservation names/phones/notes from `reservations`; with only the anon key, it imports date/status from `public_availability`.
- `SUPABASE_SYNC_ENABLED=true` can provide the initial default, but the admin switch is saved in `dbo.AppSettings` and overrides that value after it is changed.

## Deployment

1. Build the Angular frontend:

```bash
npm install
npm run build -- --base-href ./
```

2. Publish the Azure Functions API:

```bash
cd api/Ofrinio.Functions
dotnet publish -c Release
```

3. Add these Azure Function App settings:

- `AZURE_SQL_CONNECTION_STRING`
- `OFRINIO_ADMIN_TOKEN_SECRET`
- `OFRINIO_ADMIN_BOOTSTRAP_TOKEN` temporarily, only while creating admin users
- `SUPABASE_URL`
- `SUPABASE_ANON_KEY`
- `SUPABASE_SERVICE_ROLE_KEY` if names/phones/notes should be copied from Supabase
- `SUPABASE_SYNC_ENABLED` optionally, for the initial timer default

4. Bootstrap owner users with `POST /api/owner/bootstrap-users`, then delete `OFRINIO_ADMIN_BOOTSTRAP_TOKEN` from the Function App settings.

5. Push to `main`; the GitHub Pages workflow publishes `dist/ofrinio-angular-azure-calendar/browser`.

The API creates or updates its required SQL tables automatically on first use. `api/Ofrinio.Api/schema.sql` is kept as a manual reference.

The workflow at `.github/workflows/deploy-gh-pages.yml` builds with `--base-href ./` and publishes `dist/ofrinio-angular-azure-calendar/browser`.

## Additional Resources

For more information on using the Angular CLI, including detailed command references, visit the [Angular CLI Overview and Command Reference](https://angular.dev/tools/cli) page.
