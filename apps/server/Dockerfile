FROM debian:bookworm-slim AS puzzles-build
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates gcc git libc6-dev \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /build
COPY Hashi/Native/bridgegen.c Hashi/Native/upstream.sha256 ./
COPY Slitherlink/Native/loopgen.c Slitherlink/Native/upstream.sha256 ./slitherlink/
RUN git init -q source \
    && git -C source fetch --depth=1 https://git.tartarus.org/simon/puzzles.git 616da16f25da428ccda805536209095bd062676e \
    && git -C source checkout -q --detach FETCH_HEAD \
    && cd source \
    && sha256sum --strict --check ../upstream.sha256 \
    && sha256sum --strict --check ../slitherlink/upstream.sha256 \
    && mkdir -p /out/hashi /out/slitherlink \
    && cc -std=c11 -O2 -I . -o /out/hashi/bridgegen ../bridgegen.c \
       bridges.c dsf.c findloop.c malloc.c misc.c nullfe.c random.c sort.c -lm \
    && cc -std=c11 -O2 -ffunction-sections -fdata-sections -Wl,--gc-sections -I . \
       -o /out/slitherlink/loopgen ../slitherlink/loopgen.c grid.c loopgen.c penrose-legacy.c penrose.c \
       hat.c spectre.c tree234.c dsf.c findloop.c malloc.c misc.c nullfe.c random.c sort.c -lm \
    && cp LICENCE /out/hashi/LICENCE \
    && cp LICENCE /out/slitherlink/LICENCE

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY KoboLogicPuzzles.Server.csproj ./
RUN dotnet restore
COPY . ./
RUN dotnet publish -c Release --no-restore -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir /data && chown "$APP_UID" /data
COPY --from=build /out ./
COPY --from=puzzles-build /out/ ./
ENV Storage__DatabasePath=/data/kobologicpuzzles.db
ENV HashiGeneration__Enabled=true
ENV SlitherlinkGeneration__Enabled=true
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "KoboLogicPuzzles.Server.dll"]
