FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
COPY . .
RUN dotnet publish -o /app
FROM mcr.microsoft.com/dotnet/aspnet:9.0
COPY --from=build /app /app
EXPOSE 8080
ENTRYPOINT ["dotnet", "/app/Fixture.dll"]
