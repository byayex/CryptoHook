FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY ["CryptoHook.Api/CryptoHook.Api.csproj", "CryptoHook.Api/"]
COPY [".editorconfig", "."]
RUN dotnet restore "CryptoHook.Api/CryptoHook.Api.csproj"

COPY . .
WORKDIR "/src/CryptoHook.Api"
RUN dotnet build "CryptoHook.Api.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "CryptoHook.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

RUN adduser --disabled-password --gecos "" appuser && chown -R appuser /app
USER appuser

ENTRYPOINT ["dotnet", "CryptoHook.Api.dll"]
