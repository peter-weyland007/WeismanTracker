FROM mcr.microsoft.com/dotnet/sdk:11.0-preview AS build
WORKDIR /src

COPY WeismanTracker.slnx ./
COPY apps/api/api.csproj apps/api/
COPY apps/web/web.csproj apps/web/
RUN dotnet restore WeismanTracker.slnx

COPY . .
RUN dotnet publish apps/api/api.csproj -c Release -o /app/publish/api /p:UseAppHost=false
RUN dotnet publish apps/web/web.csproj -c Release -o /app/publish/web /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:11.0-preview AS runtime
WORKDIR /app

COPY --from=build /app/publish/api ./api
COPY --from=build /app/publish/web ./web
COPY docker/start.sh /app/start.sh
RUN chmod +x /app/start.sh && mkdir -p /app/data

ENV API_PORT=5199
ENV WEB_PORT=8080
ENV WeismanApi__BaseUrl=http://127.0.0.1:5199
ENV ConnectionStrings__Default=Data Source=/app/data/weismantracker.db

EXPOSE 8080
VOLUME ["/app/data"]

ENTRYPOINT ["/app/start.sh"]
