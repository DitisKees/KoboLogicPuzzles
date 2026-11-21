FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY KoboNonogram.Server.csproj ./
RUN dotnet restore
COPY . ./
RUN dotnet publish -c Release --no-restore -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
RUN mkdir /data && chown "$APP_UID" /data
COPY --from=build /out ./
ENV Storage__DatabasePath=/data/kobononogram.db
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "KoboNonogram.Server.dll"]
