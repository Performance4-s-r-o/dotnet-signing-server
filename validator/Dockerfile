# syntax=docker/dockerfile:1.7
# Validátor: build Mavenem, vlastní JRE přes jlink, běh na debian-slim jako neprivilegovaný uživatel.

FROM maven:3.9-eclipse-temurin-21 AS build
WORKDIR /src
COPY pom.xml .
RUN --mount=type=cache,target=/root/.m2 mvn -B -q dependency:go-offline
COPY src src
RUN --mount=type=cache,target=/root/.m2 mvn -B -q package -DskipTests

FROM eclipse-temurin:21-jdk AS jre
# java.desktop kvůli PDFBoxu (vizuální porovnání revizí), jdk.crypto.ec kvůli ECDSA certifikátům.
RUN jlink \
      --add-modules java.base,java.compiler,java.desktop,java.instrument,java.logging,java.management,java.naming,java.net.http,java.prefs,java.rmi,java.scripting,java.security.jgss,java.security.sasl,java.sql,java.transaction.xa,java.xml,java.xml.crypto,jdk.charsets,jdk.crypto.cryptoki,jdk.crypto.ec,jdk.management,jdk.net,jdk.unsupported,jdk.zipfs \
      --strip-debug --no-man-pages --no-header-files --compress=zip-6 \
      --output /jre

FROM debian:bookworm-slim
RUN apt-get update \
 && apt-get install -y --no-install-recommends fontconfig fonts-dejavu-core ca-certificates \
 && rm -rf /var/lib/apt/lists/* \
 && useradd --system --uid 10001 --home /app validator \
 && mkdir -p /app /var/cache/validator/tl \
 && chown -R validator /var/cache/validator
COPY --from=jre /jre /opt/jre
# Certifikáty, kterými EK podepisuje LOTL (oznámené v Úředním věstníku), z dss-demonstrations 6.5.1 (LGPL-2.1).
ADD --chmod=644 --checksum=sha256:98a76fbcd726e3553ecdf8dec59eef1b015b0579e7514d8c26f7c6124ce382e6 \
    https://raw.githubusercontent.com/esig/dss-demonstrations/6.5.1/dss-demo-webapp/src/main/resources/keystore.p12 \
    /app/oj-keystore.p12
COPY --from=build /src/target/pdf-validator.jar /app/app.jar

USER 10001
VOLUME /var/cache/validator
EXPOSE 8090
ENV JAVA_TOOL_OPTIONS="-XX:MaxRAMPercentage=75 -Djava.awt.headless=true"
ENTRYPOINT ["/opt/jre/bin/java", "-jar", "/app/app.jar"]
