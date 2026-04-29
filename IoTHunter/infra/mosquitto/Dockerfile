FROM eclipse-mosquitto:2.0.18

COPY mosquitto.conf /mosquitto/config/mosquitto.conf

RUN touch /mosquitto/config/passwordfile && \
    mosquitto_passwd -b -c /mosquitto/config/passwordfile iotgateway changeme && \
    mosquitto_passwd -b /mosquitto/config/passwordfile devicesim changeme

COPY aclfile /mosquitto/config/aclfile

RUN chmod 644 /mosquitto/config/mosquitto.conf /mosquitto/config/passwordfile /mosquitto/config/aclfile

EXPOSE 1883
EXPOSE 8083
