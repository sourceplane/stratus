# One Dockerfile for every service; the target project arrives as a build arg
# from the dotnet-service composition. Built server-side by `az acr build`, so
# no Docker daemon runs on the CI runner.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG PROJECT
WORKDIR /src
# Restore against the manifests first so a source-only change does not
# invalidate the restore layer.
COPY Directory.Build.props Directory.Packages.props Stratus.slnx ./
COPY src/ src/
COPY tooling/ tooling/
RUN dotnet restore "${PROJECT}"
# The entry assembly is renamed to a constant so the final stage can use an
# exec-form ENTRYPOINT. It has to be exec form: the chiseled runtime image ships
# no shell, so `ENTRYPOINT dotnet $VAR` would have nothing to expand it.
RUN dotnet publish "${PROJECT}" -c Release -o /app --no-restore -p:AssemblyName=app

# Chiseled: no shell, no package manager, a fraction of the CVE surface of the
# full runtime image.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS final
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "app.dll"]
