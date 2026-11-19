#!/usr/bin/env bash
# @description Удаляет контейнеры и сеть приложения. Данные, файлы проекта и образы сохраняются.
# @description_en Removes the app’s containers and network. Data, project files and images are kept.
DIR=/opt/amneziawg
SERVICE=amneziawg
IMAGE='amneziavpn/amneziawg-go'
SERVICES='amneziawg'
#@@compose-uninstall
