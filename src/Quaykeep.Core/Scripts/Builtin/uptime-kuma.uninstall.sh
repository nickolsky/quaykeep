#!/usr/bin/env bash
# @description Удаляет контейнеры и сеть приложения. Данные, файлы проекта и образы сохраняются.
# @description_en Removes the app’s containers and network. Data, project files and images are kept.
DIR=/opt/uptime-kuma
SERVICE=uptime-kuma
IMAGE='louislam/uptime-kuma'
SERVICES='uptime-kuma'
#@@compose-uninstall
