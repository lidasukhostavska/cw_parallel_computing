FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY backend/Backend.csproj ./Backend.csproj
RUN dotnet restore Backend.csproj
COPY backend/. .
RUN dotnet publish Backend.csproj -c Release -o /app/publish
COPY frontend/. /app/publish/wwwroot/

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "Backend.dll"]
