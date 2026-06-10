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

This project includes a small .NET 8 API at `api/Ofrinio.Api` that reads availability data from Azure SQL and stores booking requests in the same database.

- Configure the Azure SQL connection string with the `ConnectionStrings:OfrinioSql` setting in `api/Ofrinio.Api/appsettings.Development.json`, or set the `AZURE_SQL_CONNECTION_STRING` environment variable in production.
- The frontend reads the API base URL from `window.OFRINIO_API_BASE` in `src/index.html`.
- If the API is not configured, the app automatically falls back to a local demo calendar.

## Deployment

1. Build the Angular frontend:

```bash
npm install
npm run build -- --base-href ./
```

2. Publish the API:

```bash
cd api/Ofrinio.Api
dotnet publish -c Release
```

3. For GitHub Pages deployment:

- Push the `main` branch to GitHub.
- Use the included workflow at `.github/workflows/deploy-gh-pages.yml` to publish the site to GitHub Pages.

4. For Azure API deployment:

- Deploy the API project to an Azure App Service.
- Add the connection string secret `AZURE_SQL_CONNECTION_STRING` in Azure App Service configuration.

## Additional Resources

For more information on using the Angular CLI, including detailed command references, visit the [Angular CLI Overview and Command Reference](https://angular.dev/tools/cli) page.
