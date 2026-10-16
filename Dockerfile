FROM mcr.microsoft.com/dotnet/sdk:10.0-preview AS build
WORKDIR /src
COPY..
RUN dotnet publish -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0-preview
WORKDIR /app
COPY --from=build /app/publish.
EXPOSE 10000
ENV ASPNETCORE_URLS=http://+:10000
ENV DOTNET_EnableDiagnostics=0
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 CMD wget -qO- http://localhost:10000/health || exit 1
ENTRYPOINT ["dotnet", "IrhamTrade.Server.dll"]