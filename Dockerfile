# ---------- Stage 1: build ----------
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Спершу лише csproj -> restore кешується окремим шаром і не перезапускається при зміні коду
COPY TicketBooking.Api.csproj ./
RUN dotnet restore TicketBooking.Api.csproj

COPY . .
RUN dotnet publish TicketBooking.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false \
 && chmod -R a+rX /app/publish

# ---------- Stage 2: runtime (легкий образ без SDK) ----------
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

COPY --from=build /app/publish .

# Непривілейований користувач, передбачений в офіційному образі
USER $APP_UID

ENTRYPOINT ["dotnet", "TicketBooking.Api.dll"]
