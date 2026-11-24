FROM debian:bookworm-slim AS bridges-build
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates gcc git libc6-dev \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /build
COPY Hashi/Native/bridgegen.c Hashi/Native/upstream.sha256 ./
RUN git init -q source \
    && git -C source fetch --depth=1 https://git.tartarus.org/simon/puzzles.git 616da16f25da428ccda805536209095bd062676e \
    && git -C source checkout -q --detach FETCH_HEAD \
    && cd source \
    && sha256sum --strict --check ../upstream.sha256 \
    && mkdir /out \
    && cc -std=c11 -O2 -I . -o /out/bridgegen ../bridgegen.c \
       bridges.c dsf.c findloop.c malloc.c misc.c nullfe.c random.c sort.c -lm \
    && cp LICENCE /out/LICENCE

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
COPY --from=bridges-build /out/ ./hashi/
ENV Storage__DatabasePath=/data/kobononogram.db
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "KoboNonogram.Server.dll"]
