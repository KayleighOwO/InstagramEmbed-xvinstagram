# ─────────────────────────────────────────────────────────────────────────────
# No Node/snapsave stage anymore — the app talks to Instagram directly.
# ─────────────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS dotnet-build

WORKDIR /src

COPY InstagramEmbed.Application.csproj ./
RUN dotnet restore InstagramEmbed.Application.csproj

COPY . ./

RUN dotnet publish InstagramEmbed.Application.csproj \
    -c Release \
    -o /app/publish \
    --no-restore


FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine AS runtime

WORKDIR /app

COPY --from=dotnet-build /app/publish ./

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

ENTRYPOINT ["dotnet", "InstagramEmbed.Application.dll"]
