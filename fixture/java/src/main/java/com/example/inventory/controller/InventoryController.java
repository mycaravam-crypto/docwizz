package com.example.inventory.controller;

import com.example.inventory.domain.Item;
import com.example.inventory.service.InventoryService;
import java.util.List;
import org.springframework.http.ResponseEntity;
import org.springframework.security.access.prepost.PreAuthorize;
import org.springframework.web.bind.annotation.*;

@RestController
@RequestMapping("/api/inventory")
public class InventoryController {
    private final InventoryService inventory;

    public InventoryController(InventoryService inventory) {
        this.inventory = inventory;
    }

    /**
     * Items running low.
     * @param max most items to return
     */
    @GetMapping("/low")
    public List<Item> low(@RequestParam(defaultValue = "10") int max) {
        return inventory.lowStock();
    }

    @PreAuthorize("hasRole('ADMIN')")
    @GetMapping(path = "/{id}")
    public ResponseEntity<Item> get(@PathVariable Long id) {
        return ResponseEntity.ok(inventory.find(id));
    }
}
