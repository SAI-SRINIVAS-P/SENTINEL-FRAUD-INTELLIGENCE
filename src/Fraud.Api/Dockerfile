FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Fraud.Api/Fraud.Api.csproj src/Fraud.Api/
RUN dotnet restore src/Fraud.Api/Fraud.Api.csproj
COPY src/Fraud.Api/ src/Fraud.Api/
RUN dotnet publish src/Fraud.Api/Fraud.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    ML__DatasetPath=/app/data/raw/creditcard.csv \
    ML__ModelsDirectory=/app/artifacts/models
EXPOSE 8080
ENTRYPOINT ["dotnet", "Fraud.Api.dll"]
