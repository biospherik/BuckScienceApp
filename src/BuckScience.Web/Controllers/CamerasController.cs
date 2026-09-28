using BuckScience.Application.Abstractions;
using BuckScience.Application.Abstractions.Auth;
using BuckScience.Application.Abstractions.Services;
using BuckScience.Application.Cameras;
using BuckScience.Application.Photos;
using BuckScience.Application.Tags;
using BuckScience.Shared.Configuration;
using BuckScience.Web.Security;
using BuckScience.Web.ViewModels.Cameras;
using BuckScience.Web.ViewModels.Photos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;

namespace BuckScience.Web.Controllers;

[Authorize]
public class CamerasController : Controller
{
    private readonly IAppDbContext _db;
    private readonly GeometryFactory _geometryFactory;
    private readonly ICurrentUserService _currentUser;
    private readonly IBlobStorageService _blobStorageService;
    private readonly IWeatherService _weatherService;
    private readonly IOptions<WeatherSettings> _weatherSettings;
    private readonly ILogger<CamerasController> _logger;

    public CamerasController(
        IAppDbContext db, 
        GeometryFactory geometryFactory, 
        ICurrentUserService currentUser, 
        IBlobStorageService blobStorageService,
        IWeatherService weatherService,
        IOptions<WeatherSettings> weatherSettings,
        ILogger<CamerasController> logger)
    {
        _db = db;
        _geometryFactory = geometryFactory;
        _currentUser = currentUser;
        _blobStorageService = blobStorageService;
        _weatherService = weatherService;
        _weatherSettings = weatherSettings;
        _logger = logger;
    }

    // LIST: GET /properties/{propertyId}/cameras
    [HttpGet]
    [Route("/properties/{propertyId:int}/cameras")]
    public async Task<IActionResult> Index(int propertyId, CancellationToken ct)
    {
        if (_currentUser.Id is null) return Forbid();

        // Verify property ownership and get name for header
        var prop = await _db.Properties.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == propertyId && p.ApplicationUserId == _currentUser.Id.Value, ct);
        if (prop is null) return NotFound();

        // Uses Application handler that is property-scoped
        var items = await ListPropertyCameras.HandleAsync(_db, _currentUser.Id.Value, propertyId, ct);

        ViewBag.PropertyId = propertyId;
        ViewBag.PropertyName = prop.Name;

        var vms = items.Select(x => new CameraListItemVm
        {
            Id = x.Id,
            LocationName = x.LocationName,
            Brand = x.Brand,
            Model = x.Model,
            Latitude = x.Latitude,
            Longitude = x.Longitude,
            DirectionDegrees = x.DirectionDegrees,
            IsActive = x.IsActive,
            PhotoCount = x.PhotoCount,
            CreatedDate = x.CreatedDate
        }).ToList();

        return View(vms);
    }

    // API: GET /properties/{propertyId}/cameras/api
    [HttpGet]
    [Route("/properties/{propertyId:int}/cameras/api")]
    public async Task<IActionResult> ApiIndex(int propertyId, [FromQuery] bool includeHistory = false, CancellationToken ct = default)
    {
        if (_currentUser.Id is null) return Forbid();

        // Verify property ownership
        var prop = await _db.Properties.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == propertyId && p.ApplicationUserId == _currentUser.Id.Value, ct);
        if (prop is null) return NotFound();

        // Get cameras for this property
        var items = await ListPropertyCameras.HandleAsync(_db, _currentUser.Id.Value, propertyId, ct);

        var cameras = new List<object>();

        foreach (var camera in items)
        {
            var cameraData = new
            {
                id = camera.Id,
                locationName = camera.LocationName,
                brand = camera.Brand,
                model = camera.Model,
                latitude = camera.Latitude,
                longitude = camera.Longitude,
                directionDegrees = camera.DirectionDegrees,
                isActive = camera.IsActive,
                photoCount = camera.PhotoCount,
                createdDate = camera.CreatedDate,
                placementHistory = includeHistory ? await GetCameraPlacementHistoryForApi(camera.Id, ct) : null
            };

            cameras.Add(cameraData);
        }

        return Json(cameras);
    }

    private async Task<List<object>?> GetCameraPlacementHistoryForApi(int cameraId, CancellationToken ct)
    {
        if (_currentUser.Id is null) return null;

        var placementHistory = await GetCameraPlacementHistory.HandleAsync(_db, _currentUser.Id.Value, cameraId, ct);
        
        return placementHistory.Select(p => new
        {
            id = p.Id,
            locationName = p.LocationName,
            latitude = p.Latitude,
            longitude = p.Longitude,
            directionDegrees = p.DirectionDegrees,
            startDateTime = p.StartDateTime,
            endDateTime = p.EndDateTime,
            isCurrentPlacement = p.IsCurrentPlacement,
            isAtCurrentLocation = p.IsAtCurrentLocation,
            duration = p.Duration
        }).ToList<object>();
    }

    // CREATE: GET /properties/{propertyId}/cameras/add
    [HttpGet]
    [Route("/properties/{propertyId:int}/cameras/add")]
    public async Task<IActionResult> Create(int propertyId, CancellationToken ct)
    {
        if (_currentUser.Id is null) return Forbid();

        var property = await _db.Properties.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == propertyId && p.ApplicationUserId == _currentUser.Id.Value, ct);
        if (property is null) return NotFound();

        ViewBag.PropertyId = propertyId;
        var vm = new CameraCreateVm 
        { 
            PropertyId = propertyId,
            PropertyLatitude = property.Latitude,
            PropertyLongitude = property.Longitude,
            // Initialize camera coordinates to property center
            Latitude = property.Latitude,
            Longitude = property.Longitude
        };
        // Sync selection from degrees (defaults to North)
        vm.SyncSelectionFromDirection();
        
        return View(vm);
    }

    // CREATE: POST /properties/{propertyId}/cameras/add
    [HttpPost]
    [Route("/properties/{propertyId:int}/cameras/add")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(int propertyId, CameraCreateVm vm, CancellationToken ct)
    {
        // Sync direction from selection before validation
        vm.SyncDirectionFromSelection();
        
        if (!ModelState.IsValid)
        {
            ViewBag.PropertyId = propertyId;
            return View(vm);
        }
        if (_currentUser.Id is null) return Forbid();

        // Route is the source of truth; ensure no mismatch with payload (defense-in-depth)
        if (vm.PropertyId != propertyId)
            return BadRequest("Route and model PropertyId mismatch.");

        var id = await CreateCamera.HandleAsync(
            new CreateCamera.Command(
                vm.LocationName,
                vm.Brand,
                vm.Model,
                vm.Latitude,
                vm.Longitude,
                vm.DirectionDegrees,
                vm.IsActive),
            _db,
            _geometryFactory,
            _currentUser.Id.Value,
            propertyId,
            ct);

        TempData["CreatedId"] = id;
        return Redirect($"/properties/{propertyId}/details");
    }

    // EDIT: GET /properties/{propertyId}/cameras/{id}/edit
    [HttpGet]
    [Route("/properties/{propertyId:int}/cameras/{id:int}/edit")]
    public async Task<IActionResult> Edit(int propertyId, int id, CancellationToken ct)
    {
        if (_currentUser.Id is null) return Forbid();

        // Use explicit join to avoid LINQ translation errors with navigation properties
        var result = await _db.Cameras.AsNoTracking()
            .Include(c => c.PlacementHistories)
            .Join(_db.Properties, c => c.PropertyId, p => p.Id, (c, p) => new { Camera = c, Property = p })
            .Where(x => x.Camera.Id == id &&
                       x.Camera.PropertyId == propertyId &&
                       x.Property.ApplicationUserId == _currentUser.Id.Value)
            .Select(x => new {
                Camera = x.Camera,
                Property = x.Property,
                CurrentPlacement = x.Camera.PlacementHistories
                    .Where(ph => ph.EndDateTime == null)
                    .FirstOrDefault()
            })
            .FirstOrDefaultAsync(ct);

        if (result is null) return NotFound();

        ViewBag.PropertyId = propertyId;
        ViewBag.PropertyName = result.Property.Name;

        var vm = new CameraEditVm
        {
            PropertyId = propertyId,
            Id = result.Camera.Id,
            LocationName = result.CurrentPlacement?.LocationName ?? "",
            Brand = result.Camera.Brand,
            Model = result.Camera.Model,
            Latitude = result.CurrentPlacement?.Latitude ?? 0d,
            Longitude = result.CurrentPlacement?.Longitude ?? 0d,
            DirectionDegrees = result.CurrentPlacement?.DirectionDegrees ?? 0f,
            IsActive = result.Camera.IsActive
        };
        
        // Sync selection from degrees
        vm.SyncSelectionFromDirection();

        return View(vm);
    }

    // EDIT: POST /properties/{propertyId}/cameras/{id}/edit
    [HttpPost]
    [Route("/properties/{propertyId:int}/cameras/{id:int}/edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int propertyId, int id, CameraEditVm vm, CancellationToken ct)
    {
        // Sync direction from selection before validation
        vm.SyncDirectionFromSelection();
        
        if (!ModelState.IsValid)
        {
            ViewBag.PropertyId = propertyId;
            return View(vm);
        }
        if (_currentUser.Id is null) return Forbid();

        if (vm.PropertyId != propertyId || vm.Id != id)
            return BadRequest("Route and model identifiers mismatch.");

        // Optional pre-check for faster UX using explicit join
        var owned = await _db.Cameras.AsNoTracking()
            .Join(_db.Properties, c => c.PropertyId, p => p.Id, (c, p) => new { Camera = c, Property = p })
            .AnyAsync(x => x.Camera.Id == id &&
                          x.Camera.PropertyId == propertyId &&
                          x.Property.ApplicationUserId == _currentUser.Id.Value, ct);
        if (!owned) return NotFound();

        var ok = await UpdateCamera.HandleAsync(
            new UpdateCamera.Command(
                id,
                vm.LocationName,
                vm.Brand,
                vm.Model,
                vm.Latitude,
                vm.Longitude,
                vm.DirectionDegrees,
                vm.IsActive),
            _db,
            _geometryFactory,
            _currentUser.Id.Value,
            propertyId,
            ct);

        if (!ok) return NotFound();

        TempData["UpdatedId"] = id;
        return Redirect($"/properties/{propertyId}/details");
    }

    // DELETE (confirm): GET /properties/{propertyId}/cameras/{id}/delete
    [HttpGet]
    [Route("/properties/{propertyId:int}/cameras/{id:int}/delete")]
    public async Task<IActionResult> Delete(int propertyId, int id, CancellationToken ct)
    {
        if (_currentUser.Id is null) return Forbid();

        // Use explicit join to avoid LINQ translation errors with navigation properties
        var result = await _db.Cameras.AsNoTracking()
            .Include(c => c.PlacementHistories)
            .Join(_db.Properties, c => c.PropertyId, p => p.Id, (c, p) => new { Camera = c, Property = p })
            .Where(x => x.Camera.Id == id &&
                       x.Camera.PropertyId == propertyId &&
                       x.Property.ApplicationUserId == _currentUser.Id.Value)
            .Select(x => new {
                Camera = x.Camera,
                Property = x.Property,
                CurrentPlacement = x.Camera.PlacementHistories
                    .Where(ph => ph.EndDateTime == null)
                    .FirstOrDefault()
            })
            .FirstOrDefaultAsync(ct);

        if (result is null) return NotFound();

        var vm = new CameraDeleteVm
        {
            PropertyId = propertyId,
            Id = result.Camera.Id,
            LocationName = result.CurrentPlacement?.LocationName ?? "",
            Brand = result.Camera.Brand,
            Model = result.Camera.Model,
            Latitude = result.CurrentPlacement?.Latitude ?? 0d,
            Longitude = result.CurrentPlacement?.Longitude ?? 0d,
            IsActive = result.Camera.IsActive,
            PropertyName = result.Property.Name
        };

        ViewBag.PropertyId = propertyId;
        return View(vm);
    }

    // DELETE (execute): POST /properties/{propertyId}/cameras/{id}/delete
    [HttpPost, ActionName("Delete")]
    [Route("/properties/{propertyId:int}/cameras/{id:int}/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed(int propertyId, int id, CancellationToken ct)
    {
        if (_currentUser.Id is null) return Forbid();

        var ok = await DeleteCamera.HandleAsync(propertyId, id, _currentUser.Id.Value, _db, ct);
        if (!ok) return NotFound();

        TempData["DeletedId"] = id;
        return Redirect($"/properties/{propertyId}/details");
    }

    // UPLOAD PHOTO: GET
    [SkipSetupCheck]
    [HttpGet("/cameras/{id:int}/upload")]
    public async Task<IActionResult> UploadPhoto([FromRoute] int id, CancellationToken ct)
    {
        if (_currentUser.Id is null) return Forbid();

        // NOTE: Adjust the owner field name to your schema:
        // Use explicit join to avoid LINQ translation errors with navigation properties
        var result = await _db.Cameras
            .AsNoTracking()
            .Include(c => c.PlacementHistories)
            .Join(_db.Properties, c => c.PropertyId, p => p.Id, (c, p) => new { Camera = c, Property = p })
            .Where(x => x.Camera.Id == id && x.Property.ApplicationUserId == _currentUser.Id.Value)
            .Select(x => new {
                Camera = x.Camera,
                Property = x.Property,
                CurrentPlacement = x.Camera.PlacementHistories
                    .Where(ph => ph.EndDateTime == null)
                    .FirstOrDefault()
            })
            .FirstOrDefaultAsync(ct);

        if (result is null) return NotFound();

        ViewBag.PropertyId = result.Camera.PropertyId;
        ViewBag.CameraId = result.Camera.Id;
        ViewBag.CameraLocationName = result.CurrentPlacement?.LocationName ?? "";
        ViewBag.PropertyName = result.Property.Name;

        var vm = new PhotoUploadVm
        {
            PropertyId = result.Camera.PropertyId,
            CameraId = result.Camera.Id
        };

        return View("Upload", vm);
    }

    // PHOTOS: GET /cameras/{id}/photos (redirects to details)
    [SkipSetupCheck]
    [HttpGet("/cameras/{id:int}/photos")]
    public IActionResult Photos([FromRoute] int id)
    {
        // Redirect to the new location at camera details
        return RedirectToAction(nameof(Details), new { id });
    }

    // UPLOAD PHOTO: POST
    [SkipSetupCheck]
    [HttpPost("/cameras/{id:int}/photos/upload")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UploadPhoto([FromRoute] int id, PhotoUploadVm vm, CancellationToken ct)
    {
        if (_currentUser.Id is null) return Forbid();

        // Validate route matches model
        if (id != vm.CameraId)
            return BadRequest("Route and model CameraId mismatch.");

        if (!ModelState.IsValid)
        {
            // Reload view data for error display using explicit join
            var result = await _db.Cameras
                .AsNoTracking()
                .Include(c => c.PlacementHistories)
                .Join(_db.Properties, c => c.PropertyId, p => p.Id, (c, p) => new { Camera = c, Property = p })
                .Where(x => x.Camera.Id == id && x.Property.ApplicationUserId == _currentUser.Id.Value)
                .Select(x => new {
                    Camera = x.Camera,
                    Property = x.Property,
                    CurrentPlacement = x.Camera.PlacementHistories
                        .Where(ph => ph.EndDateTime == null)
                        .FirstOrDefault()
                })
                .FirstOrDefaultAsync(ct);

            if (result is null) return NotFound();

            ViewBag.PropertyId = result.Camera.PropertyId;
            ViewBag.CameraId = result.Camera.Id;
            ViewBag.CameraLocationName = result.CurrentPlacement?.LocationName ?? "";
            ViewBag.PropertyName = result.Property.Name;

            return View("Upload", vm);
        }

        if (vm.Files == null || !vm.Files.Any())
        {
            ModelState.AddModelError("Files", "Please select at least one file to upload.");
            
            // Reload view data for error display using explicit join
            var result = await _db.Cameras
                .AsNoTracking()
                .Include(c => c.PlacementHistories)
                .Join(_db.Properties, c => c.PropertyId, p => p.Id, (c, p) => new { Camera = c, Property = p })
                .Where(x => x.Camera.Id == id && x.Property.ApplicationUserId == _currentUser.Id.Value)
                .Select(x => new {
                    Camera = x.Camera,
                    Property = x.Property,
                    CurrentPlacement = x.Camera.PlacementHistories
                        .Where(ph => ph.EndDateTime == null)
                        .FirstOrDefault()
                })
                .FirstOrDefaultAsync(ct);

            if (result is null) return NotFound();

            ViewBag.PropertyId = result.Camera.PropertyId;
            ViewBag.CameraId = result.Camera.Id;
            ViewBag.CameraLocationName = result.CurrentPlacement?.LocationName ?? "";
            ViewBag.PropertyName = result.Property.Name;

            return View("Upload", vm);
        }

        try
        {
            // Convert IFormFile to FileData for the application layer
            var fileDataList = new List<UploadPhotos.FileData>();
            foreach (var file in vm.Files)
            {
                if (file.Length > 0)
                {
                    fileDataList.Add(new UploadPhotos.FileData(
                        file.FileName,
                        file.OpenReadStream(),
                        file.Length
                    ));
                }
            }

            var photoIds = await UploadPhotos.HandleAsync(
                new UploadPhotos.Command(vm.CameraId, fileDataList),
                _db,
                _currentUser.Id.Value,
                _blobStorageService,
                _weatherService,
                _weatherSettings,
                _logger,
                ct);

            // Return JSON response for AJAX handling
            return Json(new { success = true, photoCount = photoIds.Count, cameraId = vm.CameraId });
        }
        catch (Exception ex)
        {
            // Return error as JSON for AJAX handling
            return Json(new { success = false, error = ex.Message });
        }
    }

    // DETAILS: GET /cameras/{id}/details
    [HttpGet("/cameras/{id:int}/details")]
    public async Task<IActionResult> Details([FromRoute] int id, CancellationToken ct = default)
    {
        if (_currentUser.Id is null) return Forbid();

        var camera = await GetCameraDetails.HandleAsync(_db, _currentUser.Id.Value, id, ct);
        if (camera is null) return NotFound();

        // Get placement history
        var placementHistory = await GetCameraPlacementHistory.HandleAsync(_db, _currentUser.Id.Value, id, ct);

        var vm = new CameraDetailsVm
        {
            Id = camera.Id,
            LocationName = camera.LocationName,
            Brand = camera.Brand,
            Model = camera.Model,
            Latitude = camera.Latitude,
            Longitude = camera.Longitude,
            DirectionDegrees = camera.DirectionDegrees,
            CurrentPlacementStartDate = camera.CurrentPlacementStartDate,
            TimeAtCurrentLocation = camera.CurrentPlacementStartDate.HasValue 
                ? DateTime.UtcNow - camera.CurrentPlacementStartDate.Value 
                : null,
            IsActive = camera.IsActive,
            PhotoCount = camera.PhotoCount,
            CreatedDate = camera.CreatedDate,
            PropertyId = camera.PropertyId,
            PropertyName = camera.PropertyName,
            PropertyLatitude = camera.PropertyLatitude,
            PropertyLongitude = camera.PropertyLongitude,
            PlacementHistory = placementHistory.Select(ph => new CameraDetailsVm.PlacementHistoryItemVm
            {
                Id = ph.Id,
                LocationName = ph.LocationName,
                Latitude = ph.Latitude,
                Longitude = ph.Longitude,
                DirectionDegrees = ph.DirectionDegrees,
                StartDateTime = ph.StartDateTime,
                EndDateTime = ph.EndDateTime,
                IsCurrentPlacement = ph.IsCurrentPlacement,
                IsAtCurrentLocation = ph.IsAtCurrentLocation,
                Duration = ph.Duration
            }).ToList()
        };

        return View(vm);
    }
}