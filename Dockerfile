# Multi-stage build: SDK image to compile, slim ASP.NET runtime image to actually run.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY VisiFlow.sln .
COPY src/VisiFlow.Data/VisiFlow.Data.csproj src/VisiFlow.Data/
COPY src/VisiFlow.Api/VisiFlow.Api.csproj src/VisiFlow.Api/
RUN dotnet restore src/VisiFlow.Api/VisiFlow.Api.csproj

COPY src/ src/
RUN dotnet publish src/VisiFlow.Api/VisiFlow.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app .

# Render injects PORT and expects the service to listen on it (falls back to 10000, Render's own
# default, if PORT isn't set for some reason - e.g. running this image somewhere else).
ENV ASPNETCORE_URLS=http://0.0.0.0:10000
EXPOSE 10000

ENTRYPOINT ["sh", "-c", "ASPNETCORE_URLS=http://0.0.0.0:${PORT:-10000} dotnet VisiFlow.Api.dll"]
