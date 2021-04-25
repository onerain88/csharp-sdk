#!/bin/sh
VERSION_REGEX="^[0-9]+(\.[0-9]+){2}$"

version=$1

STORAGE_RELEASE_URL="https://github.com/leancloud/csharp-sdk/releases/download/$version/LeanCloud-SDK-Storage-Unity.zip"
REALTIME_RELEASE_URL="https://github.com/leancloud/csharp-sdk/releases/download/$version/LeanCloud-SDK-Realtime-Unity.zip"

REPO_GIT_URL="git@github.com:onerain88/csharp-sdk.git"

UNITY_PATH="/Applications/Unity/Hub/Editor/2020.3.5f1c1/Unity.app/Contents/MacOS/Unity"

UNITY_PROJECT_ASSETS_PATH=./Unity/UnityProject/Assets

# 从 Releases 下载
download() {
  releaseURL=$1
  service=$2
  # Plugins
  zipFile=`basename "$releaseURL"`
  curl -L $releaseURL -o $zipFile
  unzip $zipFile -d $service
  rm $zipFile
}

# 去掉依赖中的重复文件
diff() {
  dstPath=$1
  srcPath=$2
  for df in `ls $dstPath`:
  do
    for sf in `ls $srcPath`:
    do
      if cmp -s $dstPath/$df $srcPath/$sf
      then
        echo $dstPath/$df
        rm $dstPath/$df
        break
      fi
    done
  done
}

# 生成 package.json
package() {
  packageJson=$1
  upmPath=$2

  cat $packageJson | sed 's/__VERSION__/'$version'/' > $upmPath/package.json
}

# 生成 .meta 文件并 push 到 GitHub
deploy() {
  upmPath=$1
  tagPrefix=$1

  # 将 UPM 包移动到 Unity Project 下
  mv $upmPath/* $UNITY_PROJECT_ASSETS_PATH/

  # 使用 Unity Editor 打开工程，生成 .meta 文件
  $UNITY_PATH -batchmode -force-free -quit -nographics -silent-crashes -projectPath ./Unity/UnityProject

  mkdir $upmPath

  mv $UNITY_PROJECT_ASSETS_PATH/* $upmPath/

  # push 到 GitHub
  upmTag=$tagPrefix-$version
  cd $upmPath

  git init
  git config user.name "leancloud-bot";
  git config user.email "ci@leancloud.cn";
  git add .
  git commit -m $version;
  git tag $upmTag
  # git push origin $version
  git push -f $REPO_GIT_URL $upmTag

  cd ..
  rm -rf $upmPath
}

if [[ !($version =~ $VERSION_REGEX) ]]; then
  echo 'invalid version'
  exit
fi

upmStorage=upm-storage
upmRealtime=upm-realtime

download $STORAGE_RELEASE_URL $upmStorage
download $REALTIME_RELEASE_URL $upmRealtime

diff $upmRealtime/Plugins $upmStorage/Plugins

package ./Unity/storage.package.json $upmStorage
package ./Unity/realtime.package.json $upmRealtime

deploy $upmStorage
deploy $upmRealtime
