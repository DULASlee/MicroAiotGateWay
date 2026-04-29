param(
    [string]$Tag = "latest",
    [string]$K3sNode = "k3s-node"
)

$ErrorActionPreference = "Stop"
$images = @("iot-gateway", "backend-processor", "device-simulator", "mosquitto-iot")
$tars = @()

Write-Host "Exporting images to tar..." -ForegroundColor Cyan
foreach ($img in $images) {
    $tar = "$img-$Tag.tar"
    Write-Host "  Saving $img`:$Tag -> $tar"
    docker save -o $tar "${img}:${Tag}"
    $tars += $tar
}

Write-Host "Copying images to K3s node ($K3sNode)..." -ForegroundColor Cyan
scp $tars "${K3sNode}:/tmp/"

Write-Host "Importing images into containerd..." -ForegroundColor Cyan
ssh $K3sNode "sudo ctr -n k8s.io images import /tmp/*.tar"

Write-Host "Cleaning up local tars..." -ForegroundColor DarkYellow
Remove-Item $tars

Write-Host "Cleaning up remote tars..." -ForegroundColor DarkYellow
ssh $K3sNode "rm /tmp/*.tar"

Write-Host "Verifying images on K3s node..." -ForegroundColor Cyan
ssh $K3sNode "sudo crictl images | grep -E 'iot-gateway|backend-processor|device-simulator|mosquitto-iot'"

Write-Host "Import complete." -ForegroundColor Green
